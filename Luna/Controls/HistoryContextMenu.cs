// 左侧会话列表右键菜单（重命名 / 删除）

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Luna.Services;
using Luna.ViewModels;

namespace Luna.Controls;

/// <summary>
/// 左侧会话列表的右键上下文菜单：重命名 / 删除。
/// 视觉与交互沿用 <see cref="BubbleContextMenu"/>（鼠标位置弹出、点击外部或窗口失活时关闭），
/// 字体跟随 <see cref="ThemeService.CurrentFont"/> 全局设置。
/// </summary>
public class HistoryContextMenu
{
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly Action<ChatSessionItem> _rename;
    private readonly Action<ChatSessionItem> _delete;
    private ChatSessionItem? _session;
    private bool _windowEventsAttached;
    private Window? _window;

    public HistoryContextMenu(ListBox list, Action<ChatSessionItem> rename, Action<ChatSessionItem> delete)
    {
        _list = list;
        _rename = rename;
        _delete = delete;

        var panel = new StackPanel();
        panel.Children.Add(CreateMenuItem("重命名", (_, _) =>
        {
            var s = _session;
            CloseMenu();
            if (s is not null) _rename(s);
        }));
        panel.Children.Add(CreateMenuItem("删除", (_, _) =>
        {
            var s = _session;
            CloseMenu();
            if (s is not null) _delete(s);
        }));

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
            StaysOpen = true,
            AllowsTransparency = true,
        };

        _list.ContextMenu = null;
        _list.ContextMenuOpening += (_, e) => e.Handled = true;
        _list.PreviewMouseRightButtonDown += OnPreviewRightButtonDown;
        _list.PreviewMouseRightButtonUp += (_, e) => e.Handled = true;
        _list.Unloaded += (_, _) => Cleanup();
    }

    private void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var session = FindSession(e.OriginalSource as DependencyObject);
        if (session is null) return; // 空白处右键：不弹菜单

        e.Handled = true;

        if (_popup.IsOpen)
        {
            CloseMenu();
            return;
        }

        _session = session;
        EnsureWindowEvents();

        _popup.Placement = PlacementMode.MousePoint;
        _popup.HorizontalOffset = 0;
        _popup.VerticalOffset = 0;

        _list.Dispatcher.BeginInvoke(new Action(() => _popup.IsOpen = true),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private static ChatSessionItem? FindSession(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ListBoxItem { DataContext: ChatSessionItem item }) return item;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private void EnsureWindowEvents()
    {
        if (_windowEventsAttached) return;

        _window = Window.GetWindow(_list);
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
        if (IsInsidePopup(source)) return;
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
            if (ReferenceEquals(source, _list)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void CloseMenu() => _popup.IsOpen = false;

    private void Cleanup()
    {
        CloseMenu();

        if (!_windowEventsAttached) return;
        if (_window is not null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window.PreviewMouseDown -= OnWindowPreviewMouseDown;
        }
        _windowEventsAttached = false;
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

        button.Click += handler;
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
