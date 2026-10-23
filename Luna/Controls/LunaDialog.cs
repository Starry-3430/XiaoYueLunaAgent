// 通用自定义弹窗：圆角、可拖动、按钮动态生成、样式键切换

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Luna.Models;

namespace Luna.Controls;

public partial class LunaDialog : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // 交互式拖动：按下只记录候选，移动超过系统阈值后才真正拖动窗口。
    private bool _dragCandidate;
    private Point _dragStartPoint;
    private bool _isDragging;

    public static readonly DependencyProperty DialogTitleProperty =
        DependencyProperty.Register(nameof(DialogTitle), typeof(string), typeof(LunaDialog),
            new PropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty DialogContentProperty =
        DependencyProperty.Register(nameof(DialogContent), typeof(object), typeof(LunaDialog),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty ButtonsProperty =
        DependencyProperty.Register(nameof(Buttons), typeof(ObservableCollection<DialogButton>), typeof(LunaDialog),
            new PropertyMetadata(null, OnButtonsChanged));

    public string? DialogTitle
    {
        get => (string?)GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public object? DialogContent
    {
        get => GetValue(DialogContentProperty);
        set => SetValue(DialogContentProperty, value);
    }

    public ObservableCollection<DialogButton>? Buttons
    {
        get => (ObservableCollection<DialogButton>?)GetValue(ButtonsProperty);
        set => SetValue(ButtonsProperty, value);
    }

    public LunaDialog()
    {
        InitializeComponent();
        Buttons = [];

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dialog = (LunaDialog)d;
        dialog.TitleBlock.Text = e.NewValue as string;
        dialog.TitleBlock.Visibility = e.NewValue is string s && !string.IsNullOrEmpty(s)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dialog = (LunaDialog)d;
        dialog.ContentArea.Content = e.NewValue;
    }

    /// <summary>鼠标按下时只记录拖动候选，不立即调用 DragMove。</summary>
    private void Dialog_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = false;

        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        var src = e.OriginalSource as DependencyObject;
        var srcType = src?.GetType().FullName ?? "null";

        if (IsInsideInteractive(src))
        {
            System.Diagnostics.Debug.WriteLine($"[LunaDialog] Interactive: {srcType}");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[LunaDialog] Drag candidate: {srcType}");
        _dragStartPoint = e.GetPosition(this);
        _dragCandidate = true;
    }

    /// <summary>只有从非交互区域开始按下，并且移动超过系统拖动阈值，才拖动窗口。</summary>
    private void Dialog_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || _isDragging)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragCandidate = false;
            return;
        }

        var current = e.GetPosition(this);

        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCandidate = false;
        _isDragging = true;

        try
        {
            DragMove();
        }
        finally
        {
            _isDragging = false;
        }
    }

    private void Dialog_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = false;
    }

    /// <summary>
    /// 判断点击是否落在可交互元素上（按钮、输入框、滚动条、可滚动容器等）；这些位置不应触发窗口拖动。
    /// 任何 ScrollViewer 都视为交互区域，以覆盖 Markdown 代码块内部的横向滚动容器。
    /// </summary>
    private static bool IsInsideInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button or RepeatButton)
                return true;

            if (source is ScrollBar or Thumb or Track)
                return true;

            if (source is TextBoxBase or PasswordBox)
                return true;

            if (source is ScrollViewer)
                return true;

            // MarkdownViewer 内部可能是自绘滚动条 / 自定义交互元素，整个子树视为交互区域。
            if (IsMarkdownViewerElement(source))
                return true;

            // 兼容 ContentElement / FrameworkContentElement（VisualTreeHelper 会抛异常）
            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>
    /// 判断元素是否属于 WpfMarkdownViewer（含其内部自绘的滚动条、代码块等）。
    /// 用命名空间前缀匹配，避免在 Luna.Controls 中硬引用第三方程序集。
    /// </summary>
    private static bool IsMarkdownViewerElement(DependencyObject source)
    {
        var type = source.GetType();
        while (type is not null)
        {
            if (type.Namespace?.StartsWith("WpfMarkdownViewer", StringComparison.Ordinal) == true)
                return true;
            type = type.BaseType;
        }
        return false;
    }

    private static void OnButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dialog = (LunaDialog)d;
        if (e.OldValue is ObservableCollection<DialogButton> oldCollection)
            oldCollection.CollectionChanged -= dialog.ButtonsOnCollectionChanged;
        if (e.NewValue is ObservableCollection<DialogButton> newCollection)
            newCollection.CollectionChanged += dialog.ButtonsOnCollectionChanged;
        dialog.RebuildButtons();
    }

    private void ButtonsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildButtons();
    }

    private void RebuildButtons()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var buttons = Buttons;
            if (buttons is null || buttons.Count == 0)
            {
                ButtonsPanel.Content = null;
                return;
            }

            ButtonsPanel.Margin = new Thickness(0, 0, 0, 10);

            if (buttons.Count == 1)
            {
                // 单个按钮铺满弹窗，长度与多按钮时一致，保证英文完整显示
                var btn = MakeButton(buttons[0]);
                btn.HorizontalAlignment = HorizontalAlignment.Stretch;
                ButtonsPanel.Content = btn;
            }
            else if (buttons.Count == 2)
            {
                // 两个按钮等宽铺满弹窗，保证英文等较长文案能完整显示
                var grid = new Grid();
                for (var i = 0; i < 2; i++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (var i = 0; i < 2; i++)
                {
                    var btn = MakeButton(buttons[i]);
                    btn.Margin = i == 0 ? new Thickness(0, 0, 5, 0) : new Thickness(5, 0, 0, 0);
                    Grid.SetColumn(btn, i);
                    grid.Children.Add(btn);
                }

                ButtonsPanel.Content = grid;
            }
            else
            {
                // 三个及以上按钮纵向铺满弹窗，避免较长英文文案被截断
                var panel = new StackPanel { Orientation = Orientation.Vertical };
                foreach (var button in buttons)
                {
                    var btn = MakeButton(button);
                    btn.HorizontalAlignment = HorizontalAlignment.Stretch;
                    btn.Margin = new Thickness(0, 0, 0, 8);
                    panel.Children.Add(btn);
                }

                ButtonsPanel.Content = panel;
            }
        });
    }

    private Button MakeButton(DialogButton model)
    {
        var styleKey = !string.IsNullOrEmpty(model.StyleKey) ? model.StyleKey :
                       model.IsPrimary ? "StylePrimary" :
                       model.IsCancel ? "StyleCancel" : "StyleDefault";

        return new Button
        {
            Content = model.Text,
            Command = model.Command,
            CommandParameter = model.CommandParameter,
            Style = TryFindResource(styleKey) as Style,
            IsDefault = model.IsPrimary,
            IsCancel = model.IsCancel,
        };
    }
}