// 消息气泡的右键菜单
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Luna.Localization;
using Luna.Models;
using Luna.Services;

namespace Luna.Controls;

public class BubbleContextMenu
{
    private readonly Popup _popup;
    private readonly FrameworkElement _target;
    private bool _isOpen;
    private bool _windowEventsAttached;

    public BubbleContextMenu(FrameworkElement target)
    {
        _target = target;

        var copyButton = CreateMenuItem(LocalizationService.Instance["Common_Copy"], (_, _) => { CopyContent(); CloseMenu(); });

        var panel = new StackPanel();
        panel.Children.Add(copyButton);

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

        target.ContextMenu = null;
        target.ContextMenuOpening += (_, e) => e.Handled = true;

        target.PreviewMouseRightButtonDown += OnPreviewRightButtonDown;
        target.PreviewMouseRightButtonUp += (_, e) => e.Handled = true;

        target.Unloaded += (_, _) => Cleanup();
    }

    private void EnsureWindowEvents()
    {
        if (_windowEventsAttached) return;

        var window = Window.GetWindow(_target);
        if (window == null) return;

        window.Deactivated += OnWindowDeactivated;
        window.PreviewMouseDown += OnWindowPreviewMouseDown;
        _windowEventsAttached = true;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        CloseMenu();
    }

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
            if (ReferenceEquals(source, _target)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void CloseMenu()
    {
        _popup.IsOpen = false;
        _isOpen = false;
    }

    private void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // 右键已框选的正文：保留框选，稍后“复制”只复制框选内容；
        // 右键框选之外的正文：取消框选，稍后“复制”复制整条正文。
        if (_target is WpfMarkdownViewer.Controls.MarkdownDocumentView markdown
            && !markdown.IsPointInSelection(e.GetPosition(markdown)))
        {
            markdown.ClearSelection();
        }

        if (_popup.IsOpen)
        {
            CloseMenu();
            return;
        }

        EnsureWindowEvents();

        _popup.Placement = PlacementMode.MousePoint;
        _popup.HorizontalOffset = 0;
        _popup.VerticalOffset = 0;

        _target.Dispatcher.BeginInvoke(new Action(() =>
        {
            _popup.IsOpen = true;
            _isOpen = true;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CopyContent()
    {
        if (_target is TextBox tb)
        {
            if (tb.SelectionLength > 0)
                Clipboard.SetText(tb.SelectedText);
            else
                Clipboard.SetText(tb.Text);
            return;
        }

        // 正文有框选时只复制框选内容（与 Ctrl+C 一致）；无框选时复制整条正文。
        if (_target is WpfMarkdownViewer.Controls.MarkdownDocumentView markdown && markdown.HasSelection)
        {
            markdown.CopySelection();
            return;
        }

        if (_target.DataContext is ChatMessage msg)
            Clipboard.SetText(msg.Content);
    }

    private void Cleanup()
    {
        CloseMenu();

        if (_windowEventsAttached)
        {
            var window = Window.GetWindow(_target);
            if (window != null)
            {
                window.Deactivated -= OnWindowDeactivated;
                window.PreviewMouseDown -= OnWindowPreviewMouseDown;
            }
            _windowEventsAttached = false;
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