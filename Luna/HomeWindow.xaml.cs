using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;

namespace Luna;

/// <summary>
/// 主窗口：左侧历史会话、右侧聊天区、设置/日记/工具切换，以及 Toast 通知。
/// </summary>
public partial class HomeWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HT_CAPTION = 2;

    private readonly HomeViewModel _viewModel;
    private readonly Luna.Data.DatabaseService _databaseService;
    private readonly DiaryService _diaryService;
    private readonly DiaryViewModel _diaryViewModel;
    private readonly AttachmentManagerViewModel _attachmentManagerViewModel;
    private const int ResizeBorder = 6;             // 最大化时留出的边距，防止内容贴边
    private const int MaxToasts = 6;                // 最多同时显示的通知数量
    private const int ToastGap = 2;                 // 通知之间的间距
    private const double SlideInDistance = 40;      // 通知滑入的距离
    private const double AnimDuration = 0.3;        // 动画持续时间
    private bool _isSettingsMode;                   // 当前是否处于设置/日记/工具模式
    private Button? _activeSideButton;               // 当前激活的底部侧边按钮
    private ObservableCollection<ChatMessage>? _boundMessages; // 当前绑定自动滚动的消息集合

    private static readonly Brush DefaultSideBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xE4, 0xE0, 0xCA));
    private static readonly Brush ActiveSideBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xF2, 0xE0));

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

    public HomeWindow(HomeViewModel viewModel, AiConnectionViewModel aiConnectionViewModel,
        GeneralSettingsViewModel generalSettingsViewModel, Luna.Data.DatabaseService databaseService,
        DiaryService diaryService, DiaryViewModel diaryViewModel,
        AttachmentManagerViewModel attachmentManagerViewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _databaseService = databaseService;
        _diaryService = diaryService;
        _diaryViewModel = diaryViewModel;
        _attachmentManagerViewModel = attachmentManagerViewModel;
        AiConnectionPanel.DataContext = aiConnectionViewModel;
        GeneralSettingsPanel.DataContext = generalSettingsViewModel;
        DiaryPanel.DataContext = diaryViewModel;
        AttachmentManagerPanel.DataContext = attachmentManagerViewModel;

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

        // 切换会话时消息集合会整体更换，需要重新绑定自动滚动
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.Messages))
                BindMessages();
        };
        BindMessages();

// 左侧底部按钮：切换右侧内容区
        SettingsButton.Click += (_, _) =>
        {
            ShowSideContent(SettingsContent, "设置");
            SetActiveSideButton(SettingsButton);
            _ = _attachmentManagerViewModel.RefreshCommand.ExecuteAsync(null);
        };
        DiaryButton.Click += (_, _) =>
        {
            ShowSideContent(DiaryContent, "日记本");
            SetActiveSideButton(DiaryButton);
            _diaryService.Trigger();          // 打开日记视图时触发一次检查
            _ = _diaryViewModel.LoadAsync();  // 加载日记列表
        };
        ToolsButton.Click += (_, _) => { ShowSideContent(ToolsContent, "工具"); SetActiveSideButton(ToolsButton); };
        
        // "新对话"按钮：回到聊天模式并重置标题
        NewChatButton.Click += (_, _) => { SwitchToChat(); ChatTitle.Text = "新聊天"; HistoryList.SelectedIndex = -1; SetActiveSideButton(null); };

        // 为输入框挂载自定义右键菜单
        _ = new EditorContextMenu(InputBox);

        // 防止鼠标选中气泡文字时父级 ScrollViewer 自动滚动
        MessageScrollViewer.RequestBringIntoView += (_, e) => e.Handled = true;

        // 窗口重新激活时刷新仍在流式输出的消息渲染
        Activated += (_, _) => MarkdownViewerRefresher.RefreshStreaming(this);
    }

    /// <summary>把自动滚动挂到当前会话的消息集合上（切换会话时会更换集合）。</summary>
    private void BindMessages()
    {
        if (_boundMessages is not null)
            _boundMessages.CollectionChanged -= OnMessagesChanged;

        _boundMessages = _viewModel.Messages;
        _boundMessages.CollectionChanged += OnMessagesChanged;

        // 打开/切换会话时立即滚到底部（布局完成后再补一次，覆盖长会话与图片加载）
        ScrollMessagesToEnd();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollMessagesToEnd();
    }

    /// <summary>把消息区滚动到底部；延迟到布局完成后执行，确保内容高度已确定。</summary>
    private void ScrollMessagesToEnd()
    {
        MessageScrollViewer.Dispatcher.BeginInvoke(() =>
        {
            MessageScrollViewer.ScrollToEnd();
            MessageScrollViewer.Dispatcher.BeginInvoke(
                () => MessageScrollViewer.ScrollToEnd(),
                DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
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

    // ===== 工具页：Tavily API Key =====

    private void TavilyKeyBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.Password != _viewModel.TavilyApiKey)
            box.Password = _viewModel.TavilyApiKey;
    }

    private void TavilyKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.Password != _viewModel.TavilyApiKey)
            _viewModel.TavilyApiKey = box.Password;
    }

    private void TavilyKeyReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Parent: Panel panel } &&
            panel.Children.OfType<PasswordBox>().FirstOrDefault() is { } box)
            box.Password = string.Empty;
    }

    // ===== 气泡 Markdown 渲染 =====

    /// <summary>每个 Markdown 视图当前订阅的清理动作，避免重复 Loaded 时重复挂钩导致重复渲染。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        WpfMarkdownViewer.Controls.MarkdownDocumentView, Action> ViewerBindings = new();

    private void MessageViewer_Loaded(object sender, RoutedEventArgs e)
{
    if (sender is not WpfMarkdownViewer.Controls.MarkdownDocumentView viewer) return;
    if (viewer.DataContext is not ChatMessage msg) return;

    // 同一视图重复 Loaded 或容器复用时，先解绑上一次的订阅，防止重复渲染造成卡顿
    if (ViewerBindings.TryGetValue(viewer, out var previous))
        previous();

    _ = new BubbleContextMenu(viewer);

    System.EventHandler<WpfMarkdownViewer.Controls.LinkClickedEventArgs> linkHandler = (_, args) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Url) { UseShellExecute = true });
    viewer.LinkClicked += linkHandler;

    // ===== 构建主题（只构建一次，反复复用）=====
    var markdownStyle = WpfMarkdownViewer.Rendering.MarkdownStyle.Light with
    {
        Background = System.Windows.Media.Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
        SubtleForeground = new SolidColorBrush(Color.FromRgb(0x36, 0x2F, 0x2B)),
        EmSize = 16,
        ParagraphLineHeight = 1.4,
        HeadingScales = new[] { 1.4, 1.25, 1.15, 1.08, 1.04, 1.0 },
        QuoteBar = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
        CodeBlockBackground = new SolidColorBrush(Color.FromRgb(0xD4, 0xCF, 0xB4)),
        InlineCodeBackground = new SolidColorBrush(Color.FromRgb(0xD4, 0xCF, 0xB4)),
        Border = new SolidColorBrush(Color.FromRgb(0xD4, 0xCF, 0xB4)),
        MonoTypeface = ThemeService.CodeTypeface,
    };
    if (ThemeService.BaseTypeface is { } baseTypeface)
        markdownStyle = markdownStyle with { BaseTypeface = baseTypeface };

    // ===== 统一封装主题应用 =====
    void ApplyStyle() => viewer.ApplyTheme(markdownStyle);

    // 首次进入时先应用一次
    ApplyStyle();

    var rendered = string.Empty;

    void RenderFromScratch()
    {
        rendered = msg.Content;
        if (msg.IsStreaming)
        {
            viewer.Reset();
            ApplyStyle();                       // ★ Reset 会重置主题，必须重新应用
            if (!string.IsNullOrEmpty(rendered))
                viewer.AppendDelta(rendered);
        }
        else
        {
            viewer.SetMarkdown(rendered);
            ApplyStyle();                       // ★ SetMarkdown 也会重置，必须重新应用
        }
    }

    void AppendNewContent()
    {
        if (!viewer.IsVisible) return;

        var content = msg.Content;
        if (!content.StartsWith(rendered, StringComparison.Ordinal))
        {
            RenderFromScratch();
            return;
        }

        var delta = content[rendered.Length..];
        if (delta.Length > 0)
            viewer.AppendDelta(delta);
        rendered = content;
    }

    PropertyChangedEventHandler handler = (_, args) =>
    {
        try
        {
            if (args.PropertyName == nameof(ChatMessage.Content))
            {
                AppendNewContent();
            }
            else if (args.PropertyName == nameof(ChatMessage.IsStreaming) && !msg.IsStreaming)
            {
                if (viewer.IsVisible)
                {
                    AppendNewContent();
                    viewer.Complete();
                }
            }
        }
        catch { }
    };

    System.Windows.DependencyPropertyChangedEventHandler visibility = (_, _) =>
    {
        if (!viewer.IsVisible) return;
        // 已与最新内容同步，无需重绘（避免每次切回都整体重渲染）
        if (rendered == msg.Content) return;
        RenderFromScratch();
    };

    void Unbind()
    {
        msg.PropertyChanged -= handler;
        viewer.IsVisibleChanged -= visibility;
        viewer.LinkClicked -= linkHandler;
        viewer.Unloaded -= OnUnloaded;
    }
    void OnUnloaded(object? _, RoutedEventArgs __) => Unbind();

    msg.PropertyChanged += handler;
    viewer.IsVisibleChanged += visibility;
    viewer.Unloaded += OnUnloaded;
    ViewerBindings.AddOrUpdate(viewer, Unbind);

    RenderFromScratch();
}
    private void ReasoningScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.DataContext is not ChatMessage msg) return;

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(ChatMessage.Reasoning))
                sv.ScrollToBottom();
        };
        msg.PropertyChanged += handler;
        sv.Unloaded += (_, _) => msg.PropertyChanged -= handler;
    }

    // ===== Shift+滚轮水平滚动 =====
    private void MarkdownViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            if (IsInsideCodeBlock(e.OriginalSource as DependencyObject))
                return;

            e.Handled = true;

            if (sender is not DependencyObject element) return;
            var scrollViewer = FindChildScrollViewer(element);
            if (scrollViewer == null) return;

            SmoothScroll.Horizontal(scrollViewer, e.Delta);
        }
        else
        {
            e.Handled = true;
            var parentSv = FindAncestorScrollViewer(sender as DependencyObject);
            if (parentSv != null)
                SmoothScroll.Vertical(parentSv, e.Delta);
        }
    }

    /// <summary>ScrollViewer 自身的滚轮平滑滚动；内部可滚动控件优先处理。</summary>
    private void SmoothScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Handled) return;

        // Shift+滚轮交给内部 markdown / 代码块处理
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        // 指针位于内部可滚动控件（推理、工具结果等）上时，交给它自己处理
        if (FindAncestorScrollViewer(e.OriginalSource as DependencyObject) is { } inner
            && !ReferenceEquals(inner, sv))
            return;

        e.Handled = true;
        SmoothScroll.Vertical(sv, e.Delta);
    }

    private static bool IsInsideCodeBlock(DependencyObject? element)
    {
        while (element != null)
        {
            if (element.GetType().Name == "CodeBlockView") return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static ScrollViewer? FindChildScrollViewer(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv) return sv;
            var result = FindChildScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject? child)
    {
        while (child != null)
        {
            child = VisualTreeHelper.GetParent(child);
            if (child is ScrollViewer sv) return sv;
        }
        return null;
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
    private Border BuildToastElement(string text, Color bgColor, TranslateTransform transform, int id, Action<int> onClose)
    {
        const double toastLineHeight = 18; // 单行高度
        const int maxToastLines = 5;       // 最多显示 5 行

        var closeBlock = new TextBlock
        {
            // 关闭按钮
            Text = "✕",
            Style = TryFindResource("ToastClose") as Style,
        };

        // 内部布局：文本区（可滚动）+ 关闭按钮
        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = toastLineHeight * maxToastLines, // 最多 5 行，超出滚动
        };
        var textBlock = new TextBlock
        {
            Text = text,
            Foreground = System.Windows.Media.Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = toastLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        scrollViewer.Content = textBlock;
        Grid.SetColumn(scrollViewer, 0);
        innerGrid.Children.Add(scrollViewer);
        Grid.SetColumn(closeBlock, 1);
        innerGrid.Children.Add(closeBlock);

        // 外层 Border，使用 ToastBorder 样式并设置背景色
        var toast = new Border
        {
            Style = TryFindResource("ToastBorder") as Style,
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

    private Point? _headerDragStart;

    private void HeaderArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        _headerDragStart = e.GetPosition(this);
    }

    private void HeaderArea_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _headerDragStart is null)
            return;

        var pos = e.GetPosition(this);
        var diff = pos - _headerDragStart.Value;

        if (Math.Abs(diff.X) < 4 && Math.Abs(diff.Y) < 4)
            return;

        _headerDragStart = null;

        if (WindowState == WindowState.Maximized)
        {
            var mousePos = PointToScreen(pos);
            WindowState = WindowState.Normal;
            Left = mousePos.X - pos.X;
            Top = 0;

            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, (IntPtr)(((int)pos.Y << 16) | (int)pos.X));
        }
        else
        {
            DragMove();
        }
    }

    private void HeaderArea_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _headerDragStart = null;
    }

    // ===== 调试 =====

    /// <summary>打开日志文件夹。</summary>
    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Luna", "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            ShowToast("已打开日志文件夹：" + dir, ToastType.Success);
        }
        catch (Exception ex)
        {
            ShowToast("无法打开日志文件夹：" + ex.Message, ToastType.Error);
        }
    }

    /// <summary>清空数据库（三次确认）。</summary>
    private void ClearDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        const string body = "这将会完全删除数据库中的所有内容，包括聊天记录、日记记录等，确定要继续吗？";

        if (!ShowConfirm("警告", body + "1/3")) return;
        if (!ShowConfirm("警告", body + "2/3")) return;
        if (!ShowConfirm("警告", body + "\n这是最后一次提醒 3/3")) return;

        try
        {
            _databaseService.ClearAllData();
        }
        catch (Exception ex)
        {
            ShowToast("清空数据库失败：" + ex.Message, ToastType.Error);
            return;
        }

        ShowRestartDialog();
    }

    private bool ShowConfirm(string title, string body)
    {
        var confirmed = false;

        var dialog = new LunaDialog
        {
            Owner = this,
            DialogTitle = title,
            DialogContent = new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = "取消",
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
            new()
            {
                Text = "继续",
                StyleKey = "StyleDangerSoft",
                Command = new RelayCommand(() =>
                {
                    confirmed = true;
                    dialog.Close();
                }),
            },
        };

        dialog.ShowDialog();
        return confirmed;
    }

    private void ShowRestartDialog()
    {
        var dialog = new LunaDialog
        {
            Owner = this,
            DialogTitle = "已清除数据",
            DialogContent = new TextBlock
            {
                Text = "重新启动程序以应用",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = "重新启动",
                StyleKey = "StyleBeige",
                Command = new RelayCommand(() =>
                {
                    dialog.Close();
                    if (Application.Current is App app)
                        app.Restart();
                }),
            },
        };

        dialog.ShowDialog();
    }

    // ===== 设置项交互 =====

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

    // ===== 拖拽上传附件 =====

    private void ChatArea_DragEnter(object sender, DragEventArgs e) => UpdateDropState(e);

    private void ChatArea_DragOver(object sender, DragEventArgs e) => UpdateDropState(e);

    private void UpdateDropState(DragEventArgs e)
    {
        // 仅在聊天状态（非设置/日记/工具页）允许拖拽上传
        if (!_isSettingsMode && TryGetDroppedFiles(e, out _))
        {
            DropOverlay.Visibility = Visibility.Visible;
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void ChatArea_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void ChatArea_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (_isSettingsMode) return;
        if (!TryGetDroppedFiles(e, out var files)) return;

        e.Handled = true;
        await _viewModel.AddDroppedFilesAsync(files);
    }

    private static bool TryGetDroppedFiles(DragEventArgs e, out string[] files)
    {
        files = Array.Empty<string>();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] data || data.Length == 0) return false;

        files = data;
        return true;
    }
}