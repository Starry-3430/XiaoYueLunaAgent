using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Luna.Controls;

public class EditorContextMenu
{
    private readonly Popup _popup;
    private readonly TextBox _target;
    private readonly Button _cutButton;
    private readonly Button _copyButton;
    private readonly Button _pasteButton;
    private bool _isOpen;

    public EditorContextMenu(TextBox target)
    {
        _target = target;

        _cutButton = CreateMenuItem("剪切", (_, _) => { Cut(); CloseMenu(); });
        _copyButton = CreateMenuItem("复制", (_, _) => { Copy(); CloseMenu(); });
        _pasteButton = CreateMenuItem("粘贴", (_, _) => { Paste(); CloseMenu(); });
        
        target.Loaded += (_, _) =>
        {
            var window = Window.GetWindow(target);
            if (window != null)
            {
                window.Deactivated += (_, _) =>
                {
                    if (_popup.IsOpen) CloseMenu();
                };
            }
        };
        // 即使点到别的窗口，本窗口失去激活时也会立即关闭菜单 保险
        
        // target.PreviewMouseDown += (_, _) =>
        // {
        //     if (_popup.IsOpen && !_popup.Child.IsMouseOver)
        //         CloseMenu();
        // };

        var panel = new StackPanel();
        panel.Children.Add(_cutButton);
        panel.Children.Add(_copyButton);
        panel.Children.Add(_pasteButton);

        var container = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
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

        
        _popup.Closed += (_, _) => _isOpen = false;

        target.ContextMenu = null;
        target.ContextMenuOpening += (_, e) => e.Handled = true;

        target.PreviewMouseRightButtonDown += OnPreviewRightButtonDown;
        target.PreviewMouseRightButtonUp += (_, e) => e.Handled = true;

        // 挂窗口级事件（关菜单）
        target.Loaded += (_, _) =>
        {
            var window = Window.GetWindow(target);
            if (window == null) return;

            window.Deactivated += (_, _) => CloseMenu();
            window.PreviewMouseDown += OnWindowPreviewMouseDown ;
        };

        // 控件卸载时关掉，避免残留
        target.Unloaded += (_, _) => CloseMenu();
        // // 阻止系统默认右键菜单
        // target.ContextMenu = null;
        // target.ContextMenuOpening += (_, e) => e.Handled = true;
        //
        // target.PreviewMouseRightButtonDown += OnPreviewRightButtonDown;
        // target.PreviewMouseRightButtonUp += (_, e) => e.Handled = true;
    }
    
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_popup.IsOpen) return;

        var source = e.OriginalSource as DependencyObject;

        // 点在 Popup 内部：不关
        if (IsInsidePopup(source)) return;

        // 右键点在自己 TextBox 上：由 OnPreviewRightButtonDown 做 toggle，不在这里关
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

        // 已经打开：先关掉（或者你想重新定位也可以直接挪位置）
        if (_popup.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }

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
        if (_target.SelectionLength <= 0) return;

        Clipboard.SetText(_target.SelectedText);
        _target.SelectedText = string.Empty;
    }

    private void Copy()
    {
        if (_target.SelectionLength > 0)
            Clipboard.SetText(_target.SelectedText);
    }

    private void Paste()
    {
        if (!Clipboard.ContainsText()) return;

        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text)) return;

        _target.SelectedText = text;   // 替换当前选中内容
        _target.SelectionStart += text.Length;
        _target.SelectionLength = 0;
    }

    private void UpdateStates()
    {
        var hasSelection = _target.SelectionLength > 0;
        var canPaste = Clipboard.ContainsText();

        _cutButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        _copyButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        _pasteButton.IsEnabled = canPaste;
        _pasteButton.Opacity = canPaste ? 1.0 : 0.4;
    }

    private static Button CreateMenuItem(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Width = 60,
            Height = 20,
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            Foreground = Brushes.White,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Template = CreateButtonTemplate(),
        };

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
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)), "Bg"));
        template.Triggers.Add(hoverTrigger);

        return template;
    }
}