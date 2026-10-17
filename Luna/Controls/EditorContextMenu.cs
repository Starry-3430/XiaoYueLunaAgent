// 输入框右键菜单（剪切 / 复制 / 粘贴），替换系统默认菜单

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using Luna.Localization;
using Luna.Services;

namespace Luna.Controls;

/// <summary>
/// 为 <see cref="TextBox"/> 与 <see cref="PasswordBox"/> 提供统一的自定义右键菜单。
/// 文本类控件提供剪切 / 复制 / 粘贴；密码类控件仅提供粘贴（与系统一致，不允许复制明文）。
/// </summary>
public class EditorContextMenu
{
    private static readonly ConditionalWeakTable<Control, EditorContextMenu> Attached = new();
    private static bool _autoAttachEnabled;

    private readonly Control _target;
    private readonly TextBox? _textBox;
    private readonly PasswordBox? _passwordBox;
    private readonly Popup _popup;
    private readonly Button _cutButton;
    private readonly Button _copyButton;
    private readonly Button _pasteButton;
    private Func<bool>? _tryPasteAttachment;
    private bool _isOpen;
    private bool _windowEventsAttached;
    private Window? _window;

    /// <summary>
    /// 全局启用自动挂载：任何已加载的 <see cref="TextBox"/> / <see cref="PasswordBox"/> 都会使用自定义右键菜单。
    /// 这样无需在视图里逐个枚举（尤其对折叠后才实现、或列表模板里延迟生成的输入框同样有效）。
    /// </summary>
    public static void EnableAutoAttach()
    {
        if (_autoAttachEnabled) return;
        _autoAttachEnabled = true;

        EventManager.RegisterClassHandler(typeof(TextBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnTargetLoaded));
        EventManager.RegisterClassHandler(typeof(PasswordBox), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnTargetLoaded));
    }

    private static void OnTargetLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control control)
            Attach(control);
    }

    /// <summary>判断该控件是否支持自定义右键菜单。</summary>
    public static bool IsSupported(Control target) => target is TextBox or PasswordBox;

    /// <summary>
    /// 标记某个控件不参与全局自动挂载。消息气泡等需要专用右键菜单（<see cref="BubbleContextMenu"/>）的控件
    /// 应在 XAML 中设置为 <c>True</c>，避免被编辑框菜单覆盖。
    /// </summary>
    public static readonly DependencyProperty IsExcludedProperty = DependencyProperty.RegisterAttached(
        "IsExcluded", typeof(bool), typeof(EditorContextMenu), new PropertyMetadata(false));

    public static bool GetIsExcluded(DependencyObject d) => (bool)d.GetValue(IsExcludedProperty);
    public static void SetIsExcluded(DependencyObject d, bool value) => d.SetValue(IsExcludedProperty, value);

    /// <summary>
    /// 为指定输入框挂载自定义右键菜单；重复调用同一输入框不会重复挂钩。
    /// <paramref name="tryPasteAttachment"/> 非空时，剪贴板里是图片/文件会优先作为附件导入，
    /// 并返回 true 以阻止默认文本粘贴。
    /// </summary>
    public static void Attach(Control target, Func<bool>? tryPasteAttachment = null)
    {
        if (!IsSupported(target) || GetIsExcluded(target)) return;

        if (Attached.TryGetValue(target, out var existing))
        {
            if (tryPasteAttachment is not null)
                existing._tryPasteAttachment = tryPasteAttachment;
            return;
        }

        var menu = new EditorContextMenu(target);
        menu._tryPasteAttachment = tryPasteAttachment;
        Attached.Add(target, menu);
    }

    /// <summary>递归为子树内所有受支持的输入框挂载自定义右键菜单。</summary>
    public static void AttachTree(DependencyObject root)
    {
        if (root is Control control && IsSupported(control))
            Attach(control);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            AttachTree(VisualTreeHelper.GetChild(root, i));
    }

    public EditorContextMenu(Control target)
    {
        _target = target;
        _textBox = target as TextBox;
        _passwordBox = target as PasswordBox;

        _cutButton = CreateMenuItem(LocalizationService.Instance["Common_Cut"], (_, _) => { Cut(); CloseMenu(); });
        _copyButton = CreateMenuItem(LocalizationService.Instance["Common_Copy"], (_, _) => { Copy(); CloseMenu(); });
        _pasteButton = CreateMenuItem(LocalizationService.Instance["Common_Paste"], (_, _) => { Paste(); CloseMenu(); });

        var panel = new StackPanel();
        panel.Children.Add(_cutButton);
        panel.Children.Add(_copyButton);
        panel.Children.Add(_pasteButton);

        var container = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x43, 0x3D)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5),
            Child = panel,
        };

        _popup = new Popup
        {
            Child = container,
            Placement = PlacementMode.MousePoint,
            StaysOpen = true,          // 改为 true，避免右键事件序列导致立即关闭
            AllowsTransparency = true,
        };
        _popup.Closed += (_, _) => _isOpen = false;

        target.ContextMenu = null;
        target.ContextMenuOpening += (_, e) => e.Handled = true;

        target.PreviewMouseRightButtonDown += OnPreviewRightButtonDown;
        target.PreviewMouseRightButtonUp += (_, e) => e.Handled = true;

        // 控件卸载时关掉，避免残留
        target.Unloaded += (_, _) => CloseMenu();

        // 若构造时已处于窗口内，立即挂窗口级事件；否则在首次打开菜单时再挂。
        EnsureWindowEvents();
    }

    private void EnsureWindowEvents()
    {
        if (_windowEventsAttached) return;

        _window = Window.GetWindow(_target);
        if (_window is null) return;

        _window.Deactivated += OnWindowDeactivated;
        _window.PreviewMouseDown += OnWindowPreviewMouseDown;
        _windowEventsAttached = true;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => CloseMenu();

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_popup.IsOpen) return;

        var source = e.OriginalSource as DependencyObject;

        // 点在 Popup 内部：不关
        if (IsInsidePopup(source)) return;

        // 右键点在自己控件上：由 OnPreviewRightButtonDown 做 toggle，不在这里关
        if (e.ChangedButton == MouseButton.Right && IsInsideTarget(source)) return;

        CloseMenu();
    }

    private bool IsInsidePopup(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, _popup.Child)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private bool IsInsideTarget(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, _target)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    // 关闭右键菜单方法
    private void CloseMenu()
    {
        _popup.IsOpen = false;
        _isOpen = false;
    }

    // 右键打开逻辑
    private void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // 已经打开：先关掉
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }

        // 延迟挂载窗口级关闭事件（构造早于窗口就绪时兜底）
        EnsureWindowEvents();

        UpdateStates();

        _popup.Placement = PlacementMode.MousePoint;
        _popup.HorizontalOffset = 0;
        _popup.VerticalOffset = 0;

        // 推到当前输入事件之后再打开，避免被当作外部点击立即关闭
        _target.Dispatcher.BeginInvoke(new Action(() =>
        {
            _popup.IsOpen = true;
            _isOpen = true;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Cut()
    {
        if (_textBox is null) return;
        if (_textBox.SelectionLength > 0)
            _textBox.Cut();
    }

    private void Copy()
    {
        if (_textBox is null) return;
        if (_textBox.SelectionLength > 0)
            _textBox.Copy();
    }

    private void Paste()
    {
        // 剪贴板中是图片/文件时，优先作为附件导入（仅主输入框配置了该回调）。
        if (_tryPasteAttachment?.Invoke() == true) return;

        if (!Clipboard.ContainsText()) return;

        if (_textBox is not null)
            _textBox.Paste();
        else
            _passwordBox?.Paste();
    }

    private void UpdateStates()
    {
        // 密码框不支持选中复制，此时剪切 / 复制隐藏，仅保留粘贴。
        var hasSelection = _textBox is { SelectionLength: > 0 };
        var canPaste = CanPaste();

        _cutButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        _copyButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        _pasteButton.IsEnabled = canPaste;
        _pasteButton.Opacity = canPaste ? 1.0 : 0.4;
    }

    private static bool CanPaste()
    {
        try
        {
            return Clipboard.ContainsText()
                || Clipboard.ContainsImage()
                || Clipboard.ContainsFileDropList();
        }
        catch
        {
            return false;
        }
    }

    private static Button CreateMenuItem(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Width = 80,
            Height = 28,
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFE, 0xFA, 0xE1)),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x43, 0x3D)),
            Template = CreateButtonTemplate(),
        };

        // 跟随用户在设置中选择的全局字体
        if (ThemeService.CurrentFont is { } font)
            button.FontFamily = font;

        button.Click += handler;   // 关键：绑定点击事件
        return button;
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var borderFactory = new FrameworkElementFactory(typeof(Border), "Bg");
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        borderFactory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = borderFactory,
        };

        var hoverTrigger = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
        };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x3D, 0x36, 0x2F)), "Bg"));
        template.Triggers.Add(hoverTrigger);

        return template;
    }
}
