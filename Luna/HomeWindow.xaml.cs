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
using Luna.Localization;
using Luna.Models;
using Luna.Services;
using Luna.ViewModels;
using Microsoft.Extensions.Logging;

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

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

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
    private readonly ILogger<HomeWindow> _logger;
    private const int ResizeBorder = 6;             // 最大化时留出的边距，防止内容贴边
    private const int MaxToasts = 6;                // 最多同时显示的通知数量
    private const int ToastGap = 2;                 // 通知之间的间距
    private const double SlideInDistance = 40;      // 通知滑入的距离
    private const double AnimDuration = 0.3;        // 动画持续时间
    private bool _isSettingsMode;                   // 当前是否处于设置/日记/工具模式
    private Button? _activeSideButton;               // 当前激活的底部侧边按钮
    private ObservableCollection<ChatMessage>? _boundMessages; // 当前绑定自动滚动的消息集合
    private DispatcherTimer? _scrollThrottle;                  // 流式滚动节流计时器
    private readonly MessageRenderScheduler _renderScheduler;  // 延迟按“最新→最旧”渲染 Markdown

    private static readonly Brush DefaultSideBrush = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush ActiveSideBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x3D, 0x36, 0x2F));

    private readonly List<ToastEntry> _toastStack = []; // 当前显示的通知列表（从旧到新）
    private int _toastIdSeq; // 通知 ID 自增序列
    private readonly HistoryContextMenu _historyMenu; // 左侧历史记录右键菜单

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
        AttachmentManagerViewModel attachmentManagerViewModel, ILogger<HomeWindow> logger)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _databaseService = databaseService;
        _diaryService = diaryService;
        _diaryViewModel = diaryViewModel;
        _attachmentManagerViewModel = attachmentManagerViewModel;
        _logger = logger;

        _renderScheduler = new MessageRenderScheduler(
            isActive: () => !_isSettingsMode && IsVisible,
            indexOf: message => _boundMessages?.IndexOf(message) ?? -1,
            afterEachRender: () =>
            {
                if (IsNearBottom()) ScrollMessagesToEnd();
            });

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
                BindSelectedSession(session);
                ChatTitle.Text = session.Title;
                SwitchToChat();
            }
        };

        // 切换会话时消息集合会整体更换，需要重新绑定自动滚动
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        BindMessages();

// 左侧底部按钮：切换右侧内容区
        SettingsButton.Click += (_, _) =>
        {
            ShowSideContent(SettingsContent, LocalizationService.Instance["Home_Nav_Settings"]);
            SetActiveSideButton(SettingsButton);
            _ = _attachmentManagerViewModel.RefreshCommand.ExecuteAsync(null);
        };
        DiaryButton.Click += (_, _) =>
        {
            ShowSideContent(DiaryContent, LocalizationService.Instance["Home_Nav_Diary"]);
            SetActiveSideButton(DiaryButton);
            _diaryService.Trigger();          // 打开日记视图时触发一次检查
            _ = _diaryViewModel.LoadAsync();  // 加载日记列表
        };
        ToolsButton.Click += (_, _) => { ShowSideContent(ToolsContent, LocalizationService.Instance["Home_Nav_Tools"]); SetActiveSideButton(ToolsButton); };
        
        // "新对话"按钮：回到聊天模式并重置标题
        NewChatButton.Click += (_, _) => { BindSelectedSession(null); SwitchToChat(); ChatTitle.Text = LocalizationService.Instance["Home_NewChat"]; HistoryList.SelectedIndex = -1; SetActiveSideButton(null); };

        // 左侧历史记录右键菜单：重命名 / 删除
        _historyMenu = new HistoryContextMenu(HistoryList, RenameSession, DeleteSession);

        // 为输入框挂载自定义右键菜单（主输入框、会话搜索框，以及设置/日记页内的所有文本框）
        EditorContextMenu.Attach(InputBox, () => IsActive && _viewModel.TryPasteClipboardAttachments());
        EditorContextMenu.Attach(SearchBox);
        Loaded += (_, _) =>
        {
            EditorContextMenu.AttachTree(SettingsContent);
            EditorContextMenu.AttachTree(DiaryContent);

            _viewModel.UpdateWelcomeGreeting();

            // 主界面可能在胶囊窗生成过程中才被打开：立即刷新会话列表，
            // 让正在生成的新会话出现在历史记录中，而不是等到本轮结束。
            _ = _viewModel.RefreshSessionsAsync();
        };

        // 防止鼠标选中气泡文字时父级 ScrollViewer 自动滚动
        MessageScrollViewer.RequestBringIntoView += (_, e) => e.Handled = true;

        // 滚动位置变化时更新“滚动到底部”按钮显隐
        MessageScrollViewer.ScrollChanged += (_, _) => UpdateScrollToBottomButton();

        // 窗口是 transient（关闭后重建），关闭时把挂到单例 VM / 运行期集合上的监听解绑，避免泄漏与陈旧窗口干扰。
        Closed += (_, _) => DetachBindings();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.Messages))
            BindMessages();
    }

    private void DetachBindings()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_boundMessages is not null)
        {
            _boundMessages.CollectionChanged -= OnMessagesChanged;
            foreach (var message in _boundMessages)
                message.PropertyChanged -= OnMessagePropertyChanged;
            _boundMessages = null;
        }
        _scrollThrottle?.Stop();
        BindSelectedSession(null);
    }

    private ChatSessionItem? _titleBoundSession;

    /// <summary>订阅当前选中会话的标题变化，标题在后台生成后就地刷新顶部标题。</summary>
    private void BindSelectedSession(ChatSessionItem? session)
    {
        if (_titleBoundSession is not null)
            _titleBoundSession.PropertyChanged -= SelectedSession_PropertyChanged;
        _titleBoundSession = session;
        if (_titleBoundSession is not null)
            _titleBoundSession.PropertyChanged += SelectedSession_PropertyChanged;
    }

    private void SelectedSession_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatSessionItem.Title)) return;
        if (_isSettingsMode) return;
        if (sender is ChatSessionItem session)
            ChatTitle.Text = session.Title;
    }

    /// <summary>把自动滚动挂到当前会话的消息集合上（切换会话时会更换集合）。</summary>
    private void BindMessages()
    {
        // 会话集合整体更换：使上一轮的渲染队列失效
        _renderScheduler.Reset();

        if (_boundMessages is not null)
        {
            _boundMessages.CollectionChanged -= OnMessagesChanged;
            // 切换会话时先解绑旧集合中每条消息的属性监听，避免监听泄漏
            foreach (var message in _boundMessages)
                message.PropertyChanged -= OnMessagePropertyChanged;
        }

        _boundMessages = _viewModel.Messages;
        _boundMessages.CollectionChanged += OnMessagesChanged;

        foreach (var message in _boundMessages)
            message.PropertyChanged += OnMessagePropertyChanged;

        // 打开/切换会话时立即滚到底部
        ScrollMessagesToEnd();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新消息加入时订阅属性变化，旧消息移除时取消订阅
        if (e.NewItems != null)
            foreach (ChatMessage message in e.NewItems)
                message.PropertyChanged += OnMessagePropertyChanged;

        if (e.OldItems != null)
            foreach (ChatMessage message in e.OldItems)
                message.PropertyChanged -= OnMessagePropertyChanged;

        ScrollMessagesToEnd();
    }

    // 保持视图滚动到底部（流式内容变化时）
    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)    {
        if (e.PropertyName is not (nameof(ChatMessage.Content) or nameof(ChatMessage.Role)))
            return;

        // 用户手动上滑离开底部时停止自动滚动，避免把用户拽回底部
        if (!IsNearBottom()) return;

        // 节流：100ms 内只调度一次滚动
        if (_scrollThrottle is null)
        {
            _scrollThrottle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _scrollThrottle.Tick += (_, _) =>
            {
                _scrollThrottle.Stop();
                ScrollMessagesToEnd();
            };
        }

        if (!_scrollThrottle.IsEnabled)
            _scrollThrottle.Start();
    }

    /// <summary>消息区是否已接近底部（距底部小于 40px）。</summary>
    private bool IsNearBottom()
    {
        if (MessageScrollViewer is null) return true;
        return MessageScrollViewer.ScrollableHeight - MessageScrollViewer.VerticalOffset < 40;
    }

    /// <summary>把消息区滚动到底部；延迟到布局完成后执行，确保内容高度已确定。</summary>
    private void ScrollMessagesToEnd()
    {
        if (MessageScrollViewer is null) return;
        MessageScrollViewer.Dispatcher.BeginInvoke(
            () => MessageScrollViewer.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    /// <summary>不在底部时显示“滚动到底部”按钮，否则隐藏；非聊天界面始终隐藏。</summary>
    private void UpdateScrollToBottomButton()
    {
        if (ScrollToBottomButton is null) return;
        var show = !_isSettingsMode && !IsNearBottom();
        ScrollToBottomButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点击“滚动到底部”：回到底部并恢复对后续（含流式）内容的自动跟随。</summary>
    private void ScrollToBottom_Click(object sender, RoutedEventArgs e) => ScrollMessagesToEnd();

    /// <summary>指针悬停在按钮上时，把滚轮交给消息区处理，避免挡住滚动。</summary>
    private void ScrollToBottomButton_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (MessageScrollViewer is not null)
            SmoothScroll.Vertical(MessageScrollViewer, e.Delta);
    }

    /// <summary>左侧侧栏列表（历史会话 / 搜索结果）的滚轮平滑滚动。</summary>
    private void SideList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject element) return;
        var scrollViewer = FindChildScrollViewer(element);
        if (scrollViewer is null) return;

        e.Handled = true;
        SmoothScroll.Vertical(scrollViewer, e.Delta);
    }

    /// <summary>
    /// 显示设置/日记/工具内容区，隐藏聊天区与输入区。
    /// </summary>
    private void ShowSideContent(Grid contentGrid, string title)
    {
        _isSettingsMode = true;
        ChatTitle.Text = title;
        MessageArea.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = contentGrid == SettingsContent ? Visibility.Visible : Visibility.Collapsed;
        DiaryContent.Visibility = contentGrid == DiaryContent ? Visibility.Visible : Visibility.Collapsed;
        ToolsContent.Visibility = contentGrid == ToolsContent ? Visibility.Visible : Visibility.Collapsed;
        InputArea.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        HistoryList.SelectedIndex = -1;
        UpdateScrollToBottomButton();
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
        MessageArea.Visibility = Visibility.Visible;
        SettingsContent.Visibility = Visibility.Collapsed;
        DiaryContent.Visibility = Visibility.Collapsed;
        ToolsContent.Visibility = Visibility.Collapsed;
        InputArea.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Visible;
        SetActiveSideButton(null);
        // 聊天区可见：唤醒延迟渲染泵（按最新→最旧补齐），并重新评估滚动按钮
        _renderScheduler.Wake();
        UpdateScrollToBottomButton();
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

    // 调试日志（保留备用）：
    // _logger.LogWarning("Viewer Loaded dbId={DbId}", msg.DbId);

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

        // 流式渲染进度挂在消息上（msg.StreamingState），视图只做投影。
        // Input 优先级避免被饿死；实测单次 flush 仅 0.1–0.7ms，故与流式批次同频（16ms），
        // 让 Markdown 更新频率和“思考内容”一致（都每批刷新一次），不再每 120ms 跳一大块。
        var flushTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };

    void RenderFromScratch()
    {
        // 延迟测试（保留备用）：
        // var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            msg.StreamingState.RenderedLength = msg.Content.Length;
            msg.StreamingState.Pending = false;
            if (msg.IsStreaming)
            {
                viewer.Reset();
                ApplyStyle();                       // ★ Reset 会重置主题，必须重新应用
                // 纯空白（例如流式早期的换行）不渲染，避免空的 Markdown 块占位
                if (!string.IsNullOrWhiteSpace(msg.Content))
                    viewer.AppendDelta(msg.Content);
            }
            else
            {
                viewer.SetMarkdown(string.IsNullOrWhiteSpace(msg.Content) ? string.Empty : msg.Content);
                ApplyStyle();                       // ★ SetMarkdown 也会重置，必须重新应用
            }
        }
        finally
        {
            // 无论成功失败都结束占位，避免渲染异常时骨架永久停留
            msg.IsBodyRendered = true;

            // 延迟测试（保留备用）：
            // sw.Stop();
            // _logger.LogWarning("RenderMarkdown dbId={DbId} len={Len} elapsed={Ms}ms",
            //     msg.DbId, msg.Content.Length, sw.ElapsedMilliseconds);
        }
    }

    // 注册到延迟渲染调度器：实际渲染由调度泵按“最新→最旧”推进，避免在 Loaded 同步解析全部消息
    _renderScheduler.Register(msg, viewer, RenderFromScratch);

    void FlushPending()
    {
        if (!msg.StreamingState.Pending) return;
        // 不可见时交给调度器，等视图可用时整体补齐，避免流式正文丢失
        if (!viewer.IsVisible)
        {
            _renderScheduler.Request(msg);
            return;
        }
        msg.StreamingState.Pending = false;

        var content = msg.Content;
        if (content.Length < msg.StreamingState.RenderedLength)
        {
            RenderFromScratch();
            return;
        }

        var delta = content[msg.StreamingState.RenderedLength..];
        if (delta.Length > 0)
        {
            // 整段内容仍是空白时先不渲染（保留 RenderedLength 前移，后续真实文本照常追加）
            if (!string.IsNullOrWhiteSpace(content))
                viewer.AppendDelta(delta);
            msg.StreamingState.RenderedLength = content.Length;
        }

        msg.IsBodyRendered = true;
    }

    // delta 到来只标脏，由 flushTimer 统一节流渲染，避免每个 token 都触发重排
    PropertyChangedEventHandler handler = (_, args) =>
    {
        try
        {
            if (args.PropertyName == nameof(ChatMessage.Content))
            {
                if (viewer.IsVisible)
                {
                    // 可见：走增量刷新（16ms 节流）
                    msg.StreamingState.Pending = true;
                    if (!flushTimer.IsEnabled) flushTimer.Start();
                }
                else
                {
                    // 不可见：交给调度器，等窗口/视图可用时整体补齐，避免流式正文丢失
                    _renderScheduler.Request(msg);
                }
            }
            else if (args.PropertyName == nameof(ChatMessage.IsStreaming) && !msg.IsStreaming)
            {
                // 流式结束：可见则立即 flush 并收尾
                flushTimer.Stop();
                if (viewer.IsVisible)
                {
                    FlushPending();
                    viewer.Complete();
                }
                // 兜底：若仍未渲染出内容（视图曾不可见/未触发），交给调度器整体渲染，
                // 等价于“切换会话再回来”的恢复路径，避免正文一直缺失。
                if (!msg.IsBodyRendered || msg.StreamingState.RenderedLength != msg.Content.Length)
                    _renderScheduler.Request(msg);
            }
        }
        catch { }
    };

    flushTimer.Tick += (_, _) =>
    {
        flushTimer.Stop();
        FlushPending();
    };

    System.Windows.DependencyPropertyChangedEventHandler visibility = (_, _) =>
    {
        if (!viewer.IsVisible) return;
        // 已与最新内容同步，无需重绘（避免每次切回都整体重渲染）
        if (!msg.StreamingState.Pending && msg.StreamingState.RenderedLength == msg.Content.Length) return;
        // 交给延迟调度器：避免一次可见性切换对所有消息同步重绘造成卡顿
        _renderScheduler.Request(msg);
    };

    void Unbind()
    {
        // 调试日志（保留备用）：
        // _logger.LogWarning("Viewer Unloaded dbId={DbId}", msg.DbId);
        flushTimer.Stop();
        msg.PropertyChanged -= handler;
        viewer.IsVisibleChanged -= visibility;
        viewer.LinkClicked -= linkHandler;
        viewer.Unloaded -= OnUnloaded;
        _renderScheduler.Unregister(msg, viewer);
    }
    void OnUnloaded(object? _, RoutedEventArgs __) => Unbind();

    msg.PropertyChanged += handler;
    viewer.IsVisibleChanged += visibility;
    viewer.Unloaded += OnUnloaded;
    ViewerBindings.AddOrUpdate(viewer, Unbind);

    // 不在 Loaded 里同步渲染，交给调度器按“最新→最旧”延迟推进
    _renderScheduler.Request(msg);
}
    private void ReasoningScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (sv.DataContext is not ChatMessage msg) return;

        // 内容高度变化时自动滚到底（MaxHeight 交给 XAML，布局会自动取 min(内容, 100)）
        ScrollChangedEventHandler scrollHandler = (_, args) =>
        {
            if (args.ExtentHeightChange != 0)
                sv.ScrollToBottom();
        };
        sv.ScrollChanged += scrollHandler;

        // 展开时滚到底
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(ChatMessage.IsReasoningExpanded))
                sv.Dispatcher.BeginInvoke(() => sv.ScrollToBottom(), DispatcherPriority.Loaded);
        };
        msg.PropertyChanged += handler;

        sv.Unloaded += (_, _) =>
        {
            sv.ScrollChanged -= scrollHandler;
            msg.PropertyChanged -= handler;
        };
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
        if (sender is not FrameworkElement fe || fe.DataContext is not ChatMessage msg) return;
        var text = string.IsNullOrWhiteSpace(msg.Content) ? msg.ErrorMessage : msg.Content;
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
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
            MaxGlyph.Data = (Geometry)FindResource("IconRestore");
            RootGrid.Margin = new Thickness(ResizeBorder);
        }
        else
        {
            MaxGlyph.Data = (Geometry)FindResource("IconMaximize");
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

        if (Math.Abs(diff.X) < 2 && Math.Abs(diff.Y) < 2)
            return;

        _headerDragStart = null;

        if (WindowState == WindowState.Maximized)
        {
            // 交给系统执行原生“最大化窗口拖拽还原”：立即还原并保持鼠标与标题栏的相对位置，
            // 避免先播放还原动画再开始拖动。
            var screenPos = PointToScreen(pos);
            var hwnd = new WindowInteropHelper(this).Handle;
            var lParam = ((int)screenPos.Y & 0xFFFF) << 16 | ((int)screenPos.X & 0xFFFF);

            ReleaseCapture();
            SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, (IntPtr)lParam);
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
            ShowToast(LocalizationService.Instance.Format("Settings_OpenLogs_Opened", dir), ToastType.Success);
        }
        catch (Exception ex)
        {
            ShowToast(LocalizationService.Instance.Format("Settings_OpenLogs_Failed", ex.Message), ToastType.Error);
        }
    }

    /// <summary>清空数据库（三次确认）。</summary>
    private void ClearDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;

        if (!ShowConfirm(loc["Settings_ClearDb_Title"], loc["Settings_ClearDb_Confirm1"])) return;
        if (!ShowConfirm(loc["Settings_ClearDb_Title"], loc["Settings_ClearDb_Confirm2"])) return;
        if (!ShowConfirm(loc["Settings_ClearDb_Title"], loc["Settings_ClearDb_Confirm3"])) return;

        try
        {
            _databaseService.ClearAllData();
        }
        catch (Exception ex)
        {
            ShowToast(loc.Format("Settings_ClearDb_Failed", ex.Message), ToastType.Error);
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
                Text = LocalizationService.Instance["Dialog_Button_Cancel"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
            new()
            {
                Text = LocalizationService.Instance["Dialog_Button_Continue"],
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
            DialogTitle = LocalizationService.Instance["Settings_ClearDb_DoneTitle"],
            DialogContent = new TextBlock
            {
                Text = LocalizationService.Instance["Settings_ClearDb_DoneBody"],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = LocalizationService.Instance["Settings_ClearDb_RestartButton"],
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

    // ===== 重新启动 =====

    /// <summary>弹出确认框，确认后重新启动程序。</summary>
    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var confirmed = false;

        var dialog = new LunaDialog
        {
            Owner = this,
            DialogTitle = loc["Settings_Restart_ConfirmTitle"],
            DialogContent = new TextBlock
            {
                Text = loc["Settings_Restart_ConfirmBody"],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = loc["Dialog_Button_Cancel"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
            new()
            {
                Text = loc["Dialog_Button_OK"],
                StyleKey = "StyleBeige",
                Command = new RelayCommand(() =>
                {
                    confirmed = true;
                    dialog.Close();
                }),
            },
        };

        dialog.ShowDialog();

        if (confirmed && Application.Current is App app)
            app.Restart();
    }

    // ===== 关于 =====

    /// <summary>打开项目 GitHub 页面。</summary>
    private void AboutGitHub_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    // ===== 历史会话搜索 =====

    private int _jumpSeq;

    /// <summary>点击搜索结果：打开对应会话并滚动到命中的那条消息。</summary>
    private async void SearchResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResultsList.SelectedItem is not SearchResult result) return;
        SearchResultsList.SelectedItem = null;

        await _viewModel.OpenSessionAtMessageAsync(result);

        var session = _viewModel.ChatSessions.FirstOrDefault(s => s.Id == result.SessionId);
        if (session is not null)
        {
            ChatTitle.Text = session.Title;
            BindSelectedSession(session);
        }

        SwitchToChat();
        await ScrollToMessageAsync(result.MessageId, ++_jumpSeq);
    }

    /// <summary>把命中的消息滚动到可视区域；随延迟渲染造成的布局变化重试，直到位置稳定。</summary>
    private async Task ScrollToMessageAsync(long messageId, int seq)
    {
        var lastContentY = double.NaN;
        var stableCount = 0;

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (seq != _jumpSeq) return;

            var container = FindMessageContainer(messageId);
            if (container is not null)
            {
                var contentY = MessageScrollViewer.VerticalOffset
                    + container.TranslatePoint(new Point(0, 0), MessageScrollViewer).Y;
                var target = Math.Clamp(contentY - 80, 0, MessageScrollViewer.ScrollableHeight);
                MessageScrollViewer.ScrollToVerticalOffset(target);

                if (Math.Abs(contentY - lastContentY) < 1)
                    stableCount++;
                else
                    stableCount = 0;
                lastContentY = contentY;

                if (stableCount >= 2) return;
            }

            await Task.Delay(50);
        }
    }

    private FrameworkElement? FindMessageContainer(long messageId)
    {
        var messages = _viewModel.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].DbId != messageId) continue;
            return MessageList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
        }
        return null;
    }

    // ===== 左侧历史记录：重命名 / 删除 =====

    /// <summary>重命名会话：弹输入框，确认后写库并就地刷新标题。</summary>
    private async void RenameSession(ChatSessionItem session)
    {
        var box = new TextBox
        {
            Text = session.Title,
            Width = 260,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33)),
            Background = new SolidColorBrush(Color.FromRgb(0xD4, 0xD0, 0xBA)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
        };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        var dialog = new LunaDialog
        {
            Owner = this,
            DialogTitle = LocalizationService.Instance["Home_Session_RenameTitle"],
            DialogContent = box,
        };
        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new() { Text = LocalizationService.Instance["Dialog_Button_Cancel"], StyleKey = "StyleBeige", Command = new RelayCommand(dialog.Close) },
            new() { Text = LocalizationService.Instance["Dialog_Button_OK"], StyleKey = "StyleBeige", Command = new RelayCommand(dialog.Close) },
        };
        dialog.ShowDialog();

        var newTitle = box.Text?.Trim();
        if (!string.IsNullOrEmpty(newTitle))
            await _viewModel.RenameSessionAsync(session, newTitle);
    }

    /// <summary>删除会话：二次确认后删除全部关联数据。</summary>
    private async void DeleteSession(ChatSessionItem session)
    {
        if (!ConfirmDeleteSession(session)) return;

        await _viewModel.DeleteSessionAsync(session);

        // 若删除的是当前会话，回到新对话状态
        if (HistoryList.SelectedItem is null)
        {
            BindSelectedSession(null);
            ChatTitle.Text = LocalizationService.Instance["Home_NewChat"];
            SwitchToChat();
        }
    }

    private bool ConfirmDeleteSession(ChatSessionItem session)
    {
        var confirmed = false;

        var dialog = new LunaDialog
        {
            Owner = this,
            DialogTitle = LocalizationService.Instance["Home_Session_DeleteTitle"],
            DialogContent = new TextBlock
            {
                Text = LocalizationService.Instance.Format("Home_Session_DeleteBody", session.Title),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };
        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new() { Text = LocalizationService.Instance["Dialog_Button_Cancel"], StyleKey = "StyleBeige", Command = new RelayCommand(dialog.Close) },
            new()
            {
                Text = LocalizationService.Instance["Home_Session_DeleteButton"],
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

    // ===== 设置项交互 =====

    /// <summary>
    /// 输入框按下 Enter 时，执行发送命令（如果未按下 Shift）。
    /// </summary>
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        // 剪贴板中是图片/文件时，Ctrl+V 直接作为附件导入，而不是粘贴文本。
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && IsActive && _viewModel.TryPasteClipboardAttachments())
        {
            e.Handled = true;
            return;
        }

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