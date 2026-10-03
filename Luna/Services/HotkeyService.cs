using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;

namespace Luna.Services;

/// <summary>
/// 全局快捷键：注册系统热键，触发时回调 <see cref="Pressed"/>。
/// </summary>
public sealed class HotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4C55; // 'LU'

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ===== 底层键盘钩子（用于 Copilot 组合键）=====
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkF23 = 0x86;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private const uint VkShift = 0x10;
    private const uint VkLShift = 0xA0;
    private const uint VkRShift = 0xA1;
    private const uint VkMask = 0x11; // Ctrl：掩码键，让外壳确认 Win 参与了组合键，避免弹出开始菜单
    private const uint KeyeventfKeyup = 0x0002;
    private const uint LlkhfInjected = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private readonly ILogger<HotkeyService> _logger;
    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _registered;
    private string? _currentHotkey;

    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;
    private bool _copilotChordDown;
    private uint _heldWinVk = VkLWin;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
    }

    public event Action? Pressed;

    /// <summary>挂接到某个窗口的 HWND，并用当前配置注册热键。</summary>
    public void Attach(Window window)
    {
        if (window.IsLoaded)
        {
            AttachCore(window);
        }
        else
        {
            window.SourceInitialized += (_, _) => AttachCore(window);
        }
    }

    private void AttachCore(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        // 应用挂接前记录的快捷键
        if (!string.IsNullOrWhiteSpace(_currentHotkey))
            Update(_currentHotkey);
    }

    /// <summary>按字符串（如 Ctrl+Alt+Space）重新注册热键。</summary>
    public void Update(string hotkeyText)
    {
        _currentHotkey = hotkeyText;

        // Copilot 组合键（Win + Shift + F23）：系统会在到达注册热键前弹出搜索/Copilot，
        // 因此改用底层键盘钩子抢先拦截并吞掉按键。
        if (IsCopilotChord(hotkeyText))
        {
            Unregister();
            EnsureHookInstalled();
            return;
        }

        UninstallHook();

        if (_hwnd == IntPtr.Zero)
            return; // 窗口尚未初始化，稍后 Attach 时会重新注册

        Unregister();

        if (!TryParse(hotkeyText, out var modifiers, out var key))
            return;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
            return;

        if (RegisterHotKey(_hwnd, HotkeyId, modifiers | ModNoRepeat, vk))
        {
            _registered = true;
            _logger.LogInformation("已注册全局快捷键：{Hotkey}", hotkeyText);
        }
        else
        {
            _logger.LogWarning("注册全局快捷键失败：{Hotkey}", hotkeyText);
        }
    }

    private void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    // ===== Copilot 组合键拦截 =====

    /// <summary>当前绑定是否为 Copilot 组合键（Win + Shift + F23）。</summary>
    public static bool IsCopilotChord(string? text)
    {
        if (!TryParse(text, out var modifiers, out var key)) return false;
        return key == Key.F23 && modifiers == (ModWin | ModShift);
    }

    private void EnsureHookInstalled()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _hookProc = HookCallback;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
            _logger.LogWarning("安装全局键盘钩子失败，错误码 {Code}", Marshal.GetLastWin32Error());
        else
            _logger.LogInformation("已安装全局键盘钩子用于拦截 Copilot 组合键");
    }

    private void UninstallHook()
    {
        if (_hookHandle == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _hookProc = null;
        _copilotChordDown = false;
    }

    /// <summary>
    /// 释放被钩子吞掉抬起、残留在系统逻辑状态里的 Win 键。
    /// 若只吞掉物理抬起而不补发，外壳会一直认为 Win 处于按下状态，
    /// 导致后续单按 E 变成 Win+E（打开资源管理器）、单按 W 变成 Win+W（小组件）等。
    /// 先注入一个掩码键，让外壳判定 Win 参与了组合键而不弹出开始菜单，再合成 Win 抬起清除状态。
    /// </summary>
    private void ReleaseStuckWin()
    {
        keybd_event((byte)VkMask, 0, 0, UIntPtr.Zero);
        keybd_event((byte)VkMask, 0, KeyeventfKeyup, UIntPtr.Zero);
        keybd_event((byte)_heldWinVk, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var isDown = msg is WmKeyDown or WmSysKeyDown;
            var isUp = msg is WmKeyUp or WmSysKeyUp;
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

            // 忽略自身注入的按键，避免递归处理
            if ((data.Flags & LlkhfInjected) != 0)
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            if (data.VkCode == VkF23)
            {
                var winDown = GetAsyncKeyState((int)VkLWin) < 0 || GetAsyncKeyState((int)VkRWin) < 0;
                var shiftDown = GetAsyncKeyState((int)VkShift) < 0 ||
                                GetAsyncKeyState((int)VkLShift) < 0 ||
                                GetAsyncKeyState((int)VkRShift) < 0;

                // F23 按下且 Win、Shift 同时按住 → Copilot 组合键
                if (isDown && winDown && shiftDown)
                {
                    if (!_copilotChordDown)
                    {
                        _copilotChordDown = true;
                        _heldWinVk = GetAsyncKeyState((int)VkLWin) < 0 ? VkLWin : VkRWin;
                        Pressed?.Invoke();
                    }

                    return (IntPtr)1; // 吞掉 F23 按下
                }

                if (isDown)
                    _copilotChordDown = false; // 非组合键的 F23，清理状态

                // 组合键的 F23 抬起（即使修饰键已先松开也要吞掉）
                if (isUp && _copilotChordDown)
                    return (IntPtr)1;
            }

            // 组合键结束时吞掉物理 Win 抬起，并合成抬起把 Win 从系统逻辑状态里释放，
            // 否则 Win 会一直“卡住”，后续单按 E/W 等会误触发 Win+E/Win+W。
            if (isUp && _copilotChordDown && data.VkCode == _heldWinVk)
            {
                _copilotChordDown = false;
                ReleaseStuckWin();
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>解析快捷键字符串为修饰键与主键。</summary>
    public static bool TryParse(string? text, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var keyConverter = new KeyConverter();

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    try
                    {
                        key = (Key)keyConverter.ConvertFromString(raw)!;
                    }
                    catch
                    {
                        return false;
                    }
                    break;
            }
        }

        return key != Key.None;
    }

    /// <summary>由键盘事件生成快捷键字符串（如 Ctrl+Alt+Space）。</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var keyName = key switch
        {
            Key.Space => "Space",
            Key.OemPlus => "Plus",
            Key.OemMinus => "Minus",
            _ => key.ToString(),
        };
        parts.Add(keyName);
        return string.Join(" + ", parts);
    }
}
