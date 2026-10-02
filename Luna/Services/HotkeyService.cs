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

    private readonly ILogger<HotkeyService> _logger;
    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _registered;
    private string? _currentHotkey;

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
