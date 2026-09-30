using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Luna.Controls;
using Luna.Models;
using Luna.ViewModels;

namespace Luna;

/// <summary>
/// 主窗口：左侧历史会话、右侧聊天区、设置/日记/工具切换，以及 Toast 通知。
/// </summary>
public partial class HomeWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    private readonly HomeViewModel _viewModel;
    private const int ResizeBorder = 6;             // 最大化时留出的边距，防止内容贴边
    private const int MaxToasts = 6;                // 最多同时显示的通知数量
    private const int ToastGap = 2;                 // 通知之间的间距
    private const double SlideInDistance = 40;      // 通知滑入的距离
    private const double AnimDuration = 0.3;        // 动画持续时间
    private bool _isSettingsMode;                   // 当前是否处于设置/日记/工具模式
    private Button? _activeSideButton;               // 当前激活的底部侧边按钮

    private static readonly Brush DefaultSideBrush = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ActiveSideBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    private readonly List<ToastEntry> _toastStack = []; // 当前显示的通知列表（从旧到新）
    private int _toastIdSeq; // 通知 ID 自增序列

    /// <summary>通知类型：成功、警告、错误。</summary>
    public enum ToastType { Success, Warning, Error }

    /// <summary>单个通知的运行时数据。</summary>
    private class ToastEntry
    {
        public int Id { get; set; }
        public Border Element { get; set; } = null!;
        public TranslateTransform Transform { get; set; } = null!;
        public DispatcherTimer? AutoDismissTimer { get; set; }
        public double Height { get; set; }
    }

    public HomeWindow(HomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };

        // 历史会话列表选中项变化：切换到对应会话并更新标题
        HistoryList.SelectionChanged += (_, _) =>
        {
            if (HistoryList.SelectedItem is ChatSessionItem session)
            {
                _viewModel.SelectSessionCommand.Execute(session);
                ChatTitle.Text = session.Title;
                SwitchToChat();
            }
        };

        // 消息集合变化时，自动滚动到最底部
        _viewModel.Messages.CollectionChanged += (_, _) =>
        {
            MessageScrollViewer.Dispatcher.BeginInvoke(() =>
                MessageScrollViewer.ScrollToEnd());
        };

// 左侧底部按钮：切换右侧内容区
        SettingsButton.Click += (_, _) => { ShowSideContent(SettingsContent, "设置"); SetActiveSideButton(SettingsButton); };
        DiaryButton.Click += (_, _) => { ShowSideContent(DiaryContent, "日记本"); SetActiveSideButton(DiaryButton); };
        ToolsButton.Click += (_, _) => { ShowSideContent(ToolsContent, "工具"); SetActiveSideButton(ToolsButton); };
        
        // "新对话"按钮：回到聊天模式并重置标题
        NewChatButton.Click += (_, _) => { SwitchToChat(); ChatTitle.Text = "新聊天"; HistoryList.SelectedIndex = -1; SetActiveSideButton(null); };

        // 为输入框挂载自定义右键菜单
        _ = new EditorContextMenu(InputBox);

        // 点击窗口任意位置时，关闭所有设置项中的下拉弹出层
        PreviewMouseDown += (_, _) =>
        {
            foreach (var item in _viewModel.SettingsItems)
                if (item is SelectSetting ss) ss.IsOpen = false;
        };

        // 防止鼠标选中气泡文字时父级 ScrollViewer 自动滚动
        MessageScrollViewer.RequestBringIntoView += (_, e) => e.Handled = true;
    }

    /// <summary>
    /// 显示设置/日记/工具内容区，隐藏聊天区与输入区。
    /// </summary>
    private void ShowSideContent(Grid contentGrid, string title)
    {
        _isSettingsMode = true;
        ChatTitle.Text = title;
        MessageScrollViewer.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = contentGrid == SettingsContent ? Visibility.Visible : Visibility.Collapsed;
        DiaryContent.Visibility = contentGrid == DiaryContent ? Visibility.Visible : Visibility.Collapsed;
        ToolsContent.Visibility = contentGrid == ToolsContent ? Visibility.Visible : Visibility.Collapsed;
        InputArea.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        HistoryList.SelectedIndex = -1;
    }

    private void SetActiveSideButton(Button? active)
    {
        foreach (var btn in new[] { DiaryButton, ToolsButton, SettingsButton })
        {
            if (btn == active)
                btn.Background = ActiveSideBrush;
            else
                btn.Background = DefaultSideBrush;
        }
        _activeSideButton = active;
    }

    /// <summary>
    /// 切换到聊天模式：显示消息列表、输入区，隐藏设置等页面。
    /// </summary>
    private void SwitchToChat()
    {
        _isSettingsMode = false;
        MessageScrollViewer.Visibility = Visibility.Visible;
        SettingsContent.Visibility = Visibility.Collapsed;
        DiaryContent.Visibility = Visibility.Collapsed;
        ToolsContent.Visibility = Visibility.Collapsed;
        InputArea.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        SetActiveSideButton(null);
    }

    // ===== 气泡 Markdown 渲染 =====
    private void MessageViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMarkdownViewer.Controls.MarkdownDocumentView viewer) return;

        _ = new BubbleContextMenu(viewer);

        viewer.ApplyTheme(WpfMarkdownViewer.Rendering.MarkdownStyle.Dark with
        {
            BaseTypeface = new Typeface("Cascadia Mono"),
            Background = System.Windows.Media.Brushes.Transparent,
            EmSize = 16,
            ParagraphLineHeight = 1.4,
            HeadingScales = new[] { 1.4, 1.25, 1.15, 1.08, 1.04, 1.0 },
        });

        if (viewer.DataContext is not ChatMessage msg) return;

        viewer.SetMarkdown(msg.Content);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var dirty = false;

        timer.Tick += (_, _) =>
        {
            if (!dirty) return;
            dirty = false;
            viewer.SetMarkdown(msg.Content);
        };

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName != nameof(ChatMessage.Content)) return;
            dirty = true;
            if (!timer.IsEnabled) timer.Start();
        };

        msg.PropertyChanged += handler;

        viewer.Unloaded += (_, _) =>
        {
            msg.PropertyChanged -= handler;
            timer.Stop();
        };
    }

    // ===== 气泡右键菜单 =====
    private void MessageText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
            _ = new BubbleContextMenu(tb);
    }

    // ===== AI 操作按钮 =====
    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            Clipboard.SetText(msg.Content);
    }

    private void RewriteMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ChatMessage msg)
            _viewModel.RewriteMessageCommand.Execute(msg);
    }

    // ===== 迷你 Toast 通知 =====

    /// <summary>
    /// 在右下角显示一个 Toast 通知。
    /// </summary>
    /// <param name="text">通知内容</param>
    /// <param name="type">通知类型，决定背景色</param>
    public void ShowToast(string text, ToastType type)
    {
        var bgColor = type switch
        {
            // 根据类型选择背景色
            ToastType.Success => Color.FromRgb(0x4C, 0xAF, 0x50),
            ToastType.Warning => Color.FromRgb(0xC8, 0x8F, 0x54),
            ToastType.Error => Color.FromRgb(0xE5, 0x39, 0x35),
            _ => Color.FromRgb(0xC8, 0x8F, 0x54),
        };

        var id = ++_toastIdSeq;
        var transform = new TranslateTransform(0, SlideInDistance); // 初始位置在下方，用于滑入
        var entry = new ToastEntry { Id = id, Transform = transform };

        // 构建通知 UI，并传入关闭回调
        var toast = BuildToastElement(text, bgColor, transform, id, toastId =>
        {
            var entry = FindToastEntry(toastId);
            if (entry != null) DismissToast(entry, false);
        });
        entry.Element = toast;

        ToastStack.Children.Add(toast);

        // 测量实际高度
        toast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        entry.Height = toast.DesiredSize.Height;

        _toastStack.Add(entry);

        // 移除超出上限的最旧通知（渐变消失）
        while (_toastStack.Count >= MaxToasts)
        {
            var oldest = _toastStack[0];
            _toastStack.RemoveAt(0);
            oldest.AutoDismissTimer?.Stop();
            AnimateOpacity(oldest.Element, 0, AnimDuration, 0);
            var elem = oldest.Element;
            _ = Task.Delay((int)(AnimDuration * 1000)).ContinueWith(_ =>
                Dispatcher.Invoke(() => ToastStack.Children.Remove(elem)));
        }

        // 重新计算所有通知的理想位置
        RecalculatePositions();

        // 新通知滑入（从下方到理想位置）
        AnimateTransform(transform, GetIdealY(entry), AnimDuration, 0);
        AnimateOpacity(toast, 1, AnimDuration, 0);

        // 自动消失计时
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DismissToast(entry, false);
        };
        timer.Start();
        entry.AutoDismissTimer = timer;
    }

    /// <summary>
    /// 关闭指定通知。immediate 为 true 时立即移除，否则淡出后移除。
    /// </summary>
    /// <param name="entry">要关闭的通知条目</param>
    /// <param name="immediate">是否立即移除</param>
    private void DismissToast(ToastEntry entry, bool immediate)
    {
        if (!_toastStack.Contains(entry)) return;

        entry.AutoDismissTimer?.Stop();

        if (immediate)
        {
            RemoveToast(entry);
            return;
        }

        AnimateOpacity(entry.Element, 0, AnimDuration, 0);

        _ = Task.Delay((int)(AnimDuration * 1000)).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                if (!_toastStack.Contains(entry)) return;
                var index = _toastStack.IndexOf(entry);
                RemoveToast(entry);
            });
        });
    }

    /// <summary>
    /// 移除指定通知条目。
    /// </summary>
    /// <param name="entry">要移除的通知条目</param>
    private void RemoveToast(ToastEntry entry)
    {
        var index = _toastStack.IndexOf(entry);
        if (index < 0) return;

        _toastStack.RemoveAt(index);
        ToastStack.Children.Remove(entry.Element);

        RecalculatePositions();
    }

    /// <summary>
    /// 重新计算所有通知的理想位置。
    /// </summary>
    private void RecalculatePositions()
    {
        double yOffset = 0;
        // 从最新（列表末尾）到最旧遍历，最新通知在底部
        for (var i = _toastStack.Count - 1; i >= 0; i--)
        {
            var entry = _toastStack[i];
            AnimateTransform(entry.Transform, -yOffset, AnimDuration, 0);
            yOffset += ToastGap;
        }
    }

    /// <summary>
    /// 获取指定通知在栈中的理想 Y 偏移（用于滑入动画）。
    /// </summary>
    /// <param name="entry">要获取理想 Y 偏移的通知条目</param>
    /// <returns>理想 Y 偏移值</returns>
    private double GetIdealY(ToastEntry entry)
    {
        var index = _toastStack.IndexOf(entry);
        if (index < 0) return 0;

        double yOffset = 0;
        for (var i = _toastStack.Count - 1; i > index; i--)
            yOffset += ToastGap;
        return -yOffset;
    }

    /// <summary>
    /// 构建单个 Toast 的视觉元素（边框、文本、关闭按钮等）。
    /// </summary>
private static Border BuildToastElement(string text, Color bgColor, TranslateTransform transform, int id, Action<int> onClose)
    {
        var closeBlock = new TextBlock
        {
            // 关闭按钮
            Text = "✕",
            Style = Application.Current.TryFindResource("ToastClose") as Style,
        };

        // 内部布局：文本区（可滚动）+ 关闭按钮
        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 184, // 限制最大高度，超出滚动
        };
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = System.Windows.Media.Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        scrollViewer.Content = textBlock;
        Grid.SetColumn(scrollViewer, 0);
        innerGrid.Children.Add(scrollViewer);
        Grid.SetColumn(closeBlock, 1);
        innerGrid.Children.Add(closeBlock);

        // 外层 Border，使用 ToastBorder 样式并设置背景色
        var toast = new Border
        {
            Style = Application.Current.TryFindResource("ToastBorder") as Style,
            Background = new SolidColorBrush(bgColor),
            Child = innerGrid,
            RenderTransform = transform,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = 0, // 初始透明，由动画淡入
        };
        // 点击关闭按钮时触发回调
        closeBlock.MouseLeftButtonDown += (_, _) => onClose(id);

        return toast;
    }

    /// <summary>
    /// 根据 ID 查找通知项。
    /// </summary>
    private ToastEntry? FindToastEntry(int id) => _toastStack.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// 对 TranslateTransform 的 Y 属性执行动画。
    /// </summary>
    private static void AnimateTransform(TranslateTransform transform, double toY, double duration, double delay)
    {
        var anim = new DoubleAnimation(toY, TimeSpan.FromSeconds(duration))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new PowerEase { EasingMode = EasingMode.EaseInOut, Power = 2 },
        };
        transform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    /// <summary>
    /// 对 UIElement 的 Opacity 属性执行动画。
    /// </summary>
    private static void AnimateOpacity(UIElement element, double to, double duration, double delay)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromSeconds(duration))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
        };
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    
    // ===== 窗口按钮与拖拽 =====
    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 窗口状态变化时，更新最大化按钮图标并调整内容边距。
    /// </summary>
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxButton.Content = "❐";
            RootGrid.Margin = new Thickness(ResizeBorder);
        }
        else
        {
            MaxButton.Content = "□";
            RootGrid.Margin = new Thickness(0);
        }
    }

    /// <summary>
    /// 在最大化与正常状态之间切换。
    /// </summary>
    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>
    /// 顶部标题区鼠标按下：双击切换最大化，按住左键拖拽窗口。
    /// </summary>
    private void HeaderArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 双击标题栏切换最大化
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        // 按住左键拖拽窗口
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        // 如果当前是最大化状态，先还原，并保持鼠标相对位置
        if (WindowState == WindowState.Maximized)
        {
            var mousePos = e.GetPosition(this);
            var screenPoint = PointToScreen(mousePos);

            WindowState = WindowState.Normal;

            Left = screenPoint.X - mousePos.X;
            Top = 0;
        }

        DragMove();
    }

    // ===== 设置项交互 =====

    /// <summary>
    /// 点击下拉选择框：打开/关闭 Popup，并将 Popup 定位到当前按钮。
    /// </summary>
    private void SelectToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SelectSetting setting) return;
        e.Handled = true;

        // 设置 Popup 的 PlacementTarget 为当前按钮
        var popup = fe.Parent is Grid grid ? GetFirstChildPopup(grid) : null;
        if (popup != null)
            popup.PlacementTarget = fe;

        setting.ToggleOpenCommand.Execute(null);
    }

    /// <summary>
    /// 在视觉树中查找第一个 Popup 子元素。
    /// </summary>
    private static Popup? GetFirstChildPopup(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Popup p) return p;
        }
        return null;
    }

    /// <summary>
    /// 点击下拉选项：执行选择命令并关闭弹出层。
    /// </summary>
    private void SelectOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SelectOption option) return;
        e.Handled = true;
        // 向上查找 DataContext 为 SelectSetting 的父级
        var parent = fe;
        while (parent != null)
        {
            if (parent.DataContext is SelectSetting setting)
            {
                setting.SelectOptionCommand.Execute(option);
                break;
            }
            parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
        }
    }

    /// <summary>
    /// 数字输入框按下 Enter 时，强制更新绑定源。
    /// </summary>
    private void NumberInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox)
        {
            var expr = textBox.GetBindingExpression(TextBox.TextProperty);
            expr?.UpdateSource();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 输入框按下 Enter 时，执行发送命令（如果未按下 Shift）。
    /// </summary>
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (_viewModel.SendCommand.CanExecute(null))
                _viewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}