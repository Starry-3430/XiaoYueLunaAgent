using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfMarkdownViewer.Rendering;

namespace WpfMarkdownViewer.Controls;

/// <summary>
/// The optional, thin Conversation Shell (CONTEXT.md: "Conversation Shell"; ADR-0004). It composes many
/// Documents into a transcript by stacking one <see cref="MarkdownDocumentView"/> per message and owns the
/// message-level concerns the core deliberately leaves out: per-role chrome (ChatGPT-style — user turns are
/// right-aligned bubbles, assistant turns full-width) and, together with <see cref="MarkdownScrollHost"/>,
/// autoscroll across the whole transcript. The core still renders exactly one Document per message.
/// </summary>
/// <remarks>
/// Non-scrolling content: like <see cref="MarkdownDocumentView"/> it is meant to live inside a
/// <see cref="MarkdownScrollHost"/>. The streaming methods (<see cref="StartMessage"/>,
/// <see cref="AppendDelta"/>, <see cref="CompleteMessage"/>) are expected on the UI thread; each message's
/// own renderer handles the adaptive flush cadence. Block-level virtualization (ADR-0006) keeps working
/// inside each message because the shell forwards the host viewport down to every realized message view.
/// </remarks>
[System.Windows.Markup.ContentProperty(nameof(MessagesHost))]
public class ConversationView : Panel, IVirtualizingContent, IScrollHostAware
{
    private const double UserBubbleMaxWidthFraction = 0.82;
    private const double UserBubbleRightMargin = 16;
    private const double VirtualizationBuffer = 600;     // realize a margin beyond the viewport to avoid pop-in
    private const double EstimatedMessageHeight = 120;   // provisional height for a never-yet-measured message

    private sealed class MessageSlot
    {
        public required ChatRole Role { get; init; }
        public readonly StringBuilder Markdown = new();
        public FrameworkElement? Element { get; set; }   // realized container (content + action bar)
        public MarkdownDocumentView? View { get; set; }  // the inner renderer
        public Border? Bubble { get; set; }              // the user bubble, if any (for MaxWidth)
        public FrameworkElement? ActionBar { get; set; } // the hover/always-on action row
        public double Height { get; set; }
        public double Y { get; set; }
        public bool Finalized { get; set; }
        public bool IsActive { get; set; }               // the trailing message currently receiving tokens
    }

    private readonly List<MessageSlot> _slots = new();
    private MarkdownStyle _style = MarkdownStyle.Light;
    private double _messageSpacing = 22;

    private double _viewportTop;
    private double _viewportHeight; // 0 ⇒ no Scroll Host connected

    // One controller spanning every message's text leaves, so a drag selects across message boundaries (ADR-0008).
    private readonly SelectionController _selection;

    public ConversationView()
    {
        Background = _style.Background;
        Focusable = true;
        _selection = new SelectionController(this);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection()));
        BuildContextMenu();
        // Each message's MarkdownDocumentView manages its own timers on Unloaded; only the shell-level drag needs ending here.
        Unloaded += (_, _) => { if (_selection.IsDragging) _selection.End(); };
    }

    private MenuItem? _copyMenuItem;

    private void BuildContextMenu()
    {
        _copyMenuItem = new MenuItem { Header = "复制" };
        _copyMenuItem.Click += (_, _) => CopySelection();
        var selectAll = new MenuItem { Header = "全选" };
        selectAll.Click += (_, _) => SelectAll();

        var menu = new ContextMenu();
        menu.Items.Add(_copyMenuItem);
        menu.Items.Add(selectAll);
        ContextMenu = menu;
        ContextMenuOpening += (_, _) => { if (_copyMenuItem is not null) _copyMenuItem.IsEnabled = _selection.HasSelection; };
    }

    /// <summary>XAML content sink placeholder so the control can be declared with no children. Not used in code.</summary>
    public UIElementCollection MessagesHost => InternalChildren;

    /// <summary>The appearance applied to every message (assistant full-width, user bubble derives from it). Runtime-swappable.</summary>
    public MarkdownStyle MarkdownStyle
    {
        get => _style;
        set => ApplyTheme(value);
    }

    /// <summary>When false, every message realizes all its Blocks (e.g. for printing/snapshots). Default true.</summary>
    public bool VirtualizationEnabled { get; set; } = true;

    /// <summary>Number of messages in the transcript.</summary>
    public int MessageCount => _slots.Count;

    /// <summary>When true every message's action bar stays visible; otherwise it shows on hover. Default false.</summary>
    public bool AlwaysShowActions { get; set; }

    /// <summary>Raised when the user activates a link in any message. The host decides whether/how to navigate.</summary>
    public event EventHandler<LinkClickedEventArgs>? LinkClicked;

    /// <summary>Raised after a streaming message is completed (<see cref="CompleteMessage"/>).</summary>
    public event EventHandler? MessageCompleted;

    /// <summary>Raised when the user clicks "regenerate" on an assistant message. The host re-streams the response.</summary>
    public event EventHandler<MessageActionEventArgs>? MessageRegenerateRequested;

    // --- Streaming API (UI thread) ---

    /// <summary>Begin a new trailing message; finalizes the previous active one. Tokens then flow via <see cref="AppendDelta"/>.</summary>
    public void StartMessage(ChatRole role)
    {
        CompleteMessage();
        var slot = new MessageSlot { Role = role, IsActive = true };
        Realize(slot);              // the active message is always realized (never virtualized), like the Active Block
        _slots.Add(slot);
        InvalidateMeasure();
    }

    /// <summary>Append a streamed token/delta to the active message.</summary>
    public void AppendDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;
        var active = ActiveSlot();
        if (active is null)
        {
            StartMessage(ChatRole.Assistant); // tolerate AppendDelta before StartMessage
            active = ActiveSlot()!;
        }
        active.Markdown.Append(delta);
        active.View!.AppendDelta(delta);
    }

    /// <summary>Finalize the active message (Markdig becomes authoritative for it). No-op if none is active.</summary>
    public void CompleteMessage()
    {
        var active = ActiveSlot();
        if (active is null)
            return;
        active.View!.Complete();
        active.Finalized = true;
        active.IsActive = false;
        MessageCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Add an already-complete message with no token stream (static path).</summary>
    public void AddMessage(ChatRole role, string markdown)
    {
        CompleteMessage();
        var slot = new MessageSlot { Role = role, Finalized = true };
        slot.Markdown.Append(markdown);
        Realize(slot); // realized eagerly; MeasureOverride virtualizes it away if it lands off-screen
        _slots.Add(slot);
        InvalidateMeasure();
    }

    /// <summary>Remove every message and reset the transcript.</summary>
    public void Clear()
    {
        InternalChildren.Clear();
        _slots.Clear();
        InvalidateMeasure();
    }

    /// <summary>Switch the appearance at runtime; restyles every realized message and its chrome in place.</summary>
    public void ApplyTheme(MarkdownStyle theme)
    {
        _style = theme ?? throw new ArgumentNullException(nameof(theme));
        Background = _style.Background;
        foreach (var slot in _slots)
        {
            // Restyle in place: the message view re-creates its Block visuals from the Blocks it already
            // parsed (no re-parse), and a streaming message keeps streaming. Virtualized-away messages have
            // no visuals and are realized with the new style later.
            slot.View?.ApplyTheme(StyleForRole(slot.Role));
            if (slot.Bubble is { } bubble)
                bubble.Background = _style.UserBubbleBackground;
            if (slot.ActionBar is Panel bar)
            {
                foreach (var button in bar.Children.OfType<Button>())
                    button.Foreground = _style.SubtleForeground;
            }
        }
        InvalidateMeasure();
    }

    private MessageSlot? ActiveSlot() => _slots.Count > 0 && _slots[^1].IsActive ? _slots[^1] : null;

    // --- Realization / chrome ---

    private void Realize(MessageSlot slot)
    {
        var view = new MarkdownDocumentView
        {
            MarkdownStyle = StyleForRole(slot.Role),
            VirtualizationEnabled = VirtualizationEnabled,
            ShrinkToContentWidth = slot.Role == ChatRole.User, // user bubbles hug their content
            SelectionEnabled = false, // the shell owns one selection spanning all messages
        };
        view.LinkClicked += OnChildLinkClicked;
        slot.View = view;

        FrameworkElement content = view;
        if (slot.Role == ChatRole.User)
        {
            slot.Bubble = new Border
            {
                Background = _style.UserBubbleBackground,
                CornerRadius = new CornerRadius(14),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = view,
            };
            content = slot.Bubble;
        }

        // Stack the message content above its action bar; both align to the message's side.
        bool user = slot.Role == ChatRole.User;
        var actionBar = BuildActionBar(slot);
        slot.ActionBar = actionBar;
        var container = new StackPanel
        {
            Margin = user ? new Thickness(0, 0, UserBubbleRightMargin, 0) : default,
        };
        container.Children.Add(content);
        container.Children.Add(actionBar);
        if (!AlwaysShowActions)
        {
            container.MouseEnter += (_, _) => actionBar.Visibility = Visibility.Visible;
            container.MouseLeave += (_, _) => actionBar.Visibility = Visibility.Hidden;
        }

        slot.Element = container;
        InternalChildren.Add(container);

        // A finalized message owns its full Markdown, so it can be rebuilt verbatim after being virtualized
        // away and scrolled back. The active (streaming) message is fed live and is never virtualized.
        if (slot.Finalized)
            view.SetMarkdown(slot.Markdown.ToString());
    }

    private FrameworkElement BuildActionBar(MessageSlot slot)
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 4, 2, 0),
            HorizontalAlignment = slot.Role == ChatRole.User ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            // Hidden keeps the action row's measured height stable. Collapsed made every hover move
            // all following messages, which could repeatedly retrigger hover during streaming layout.
            Visibility = AlwaysShowActions ? Visibility.Visible : Visibility.Hidden,
        };
        bar.Children.Add(ActionButton("复制", () => CopyMessage(slot)));
        if (slot.Role == ChatRole.Assistant)
            bar.Children.Add(ActionButton("重新生成", () => RaiseRegenerate(slot)));
        return bar;
    }

    private Button ActionButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _style.SubtleForeground,
            Cursor = Cursors.Hand,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void CopyMessage(MessageSlot slot)
    {
        try
        {
            var data = new DataObject();
            data.SetText(slot.Markdown.ToString(), TextDataFormat.UnicodeText);
            Clipboard.SetDataObject(data, true);
        }
        catch { /* clipboard busy */ }
    }

    private void RaiseRegenerate(MessageSlot slot)
    {
        int index = _slots.IndexOf(slot);
        if (index >= 0)
            MessageRegenerateRequested?.Invoke(this, new MessageActionEventArgs(index, slot.Role));
    }

    private void Devirtualize(MessageSlot slot)
    {
        if (slot.Element is not null)
            InternalChildren.Remove(slot.Element);
        if (slot.View is not null)
            slot.View.LinkClicked -= OnChildLinkClicked;
        slot.Element = null;
        slot.View = null;
        slot.Bubble = null;
        slot.ActionBar = null;
    }

    // User bubble: the Border supplies the inset and background, so the inner view drops its padding and goes transparent.
    private MarkdownStyle StyleForRole(ChatRole role) => role == ChatRole.User
        ? _style with { ContentPadding = new Thickness(16, 10, 16, 10), Background = Brushes.Transparent }
        : _style;

    private void OnChildLinkClicked(object? sender, LinkClickedEventArgs e) => LinkClicked?.Invoke(this, e);

    void IVirtualizingContent.SetViewport(double top, double height)
    {
        _viewportTop = top;
        _viewportHeight = height;
        InvalidateMeasure();
    }

    void IScrollHostAware.AttachScroll(Action<double> scrollByVertical) =>
        _selection.EnableAutoScroll(() => (_viewportTop, _viewportHeight), scrollByVertical);

    // --- Cross-message selection (ADR-0008): one controller over every realized message's text leaves ---

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled) // a link or code-copy button inside a message handled it
            return;
        if (_selection.Begin(e.GetPosition(this)))
        {
            Focus();
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_selection.IsDragging)
            _selection.Update(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selection.IsDragging)
            return;
        _selection.End();
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Capture lost mid-drag (Alt+Tab, window closed): end the drag so the auto-scroll timer stops.
        if (_selection.IsDragging)
            _selection.End();
    }

    /// <summary>Select all text across every realized message.</summary>
    public void SelectAll() => _selection.SelectAll();

    /// <summary>Copy the current cross-message selection as plain-text Markdown.</summary>
    public void CopySelection() => _selection.Copy();

    // --- Layout ---

    protected override Size MeasureOverride(Size availableSize)
    {
        double availW = availableSize.Width;
        double contentW = Math.Max(1, double.IsInfinity(availW) ? 800 : availW);

        bool virtualize = VirtualizationEnabled && _viewportHeight > 0;
        double bufTop = _viewportTop - VirtualizationBuffer;
        double bufBottom = _viewportTop + _viewportHeight + VirtualizationBuffer;

        // Pass 1: provisional Y from cached/estimated heights, then realize/devirtualize per viewport. Only
        // finalized messages may be dropped; the active (streaming) message is never virtualized.
        double y = 0;
        foreach (var slot in _slots)
        {
            slot.Y = y;
            y += (slot.Height > 0 ? slot.Height : EstimatedMessageHeight) + _messageSpacing;
        }
        foreach (var slot in _slots)
        {
            bool onScreen = !virtualize || slot.IsActive || !slot.Finalized || slot.Height <= 0
                || (slot.Y <= bufBottom && slot.Y + Math.Max(slot.Height, EstimatedMessageHeight) >= bufTop);
            if (onScreen && slot.View is null)
                Realize(slot);
            else if (!onScreen && slot.View is not null && slot.Finalized)
                Devirtualize(slot);
        }

        // Pass 2: forward the sub-viewport (two-level virtualization, ADR-0006), measure realized messages,
        // cache their heights, and compute final layout.
        y = 0;
        double maxW = 0;
        foreach (var slot in _slots)
        {
            slot.Y = y;

            if (slot.Bubble is { } bubble)
                bubble.MaxWidth = contentW * UserBubbleMaxWidthFraction;
            if (slot.View is IVirtualizingContent vc && _viewportHeight > 0)
                vc.SetViewport(_viewportTop - y, _viewportHeight);

            if (slot.Element is { } el)
            {
                el.Measure(new Size(contentW, double.PositiveInfinity));
                slot.Height = el.DesiredSize.Height;
                maxW = Math.Max(maxW, el.DesiredSize.Width);
            }
            else if (slot.Height <= 0)
            {
                slot.Height = EstimatedMessageHeight;
            }

            y += slot.Height + _messageSpacing;
        }

        double height = _slots.Count > 0 ? y - _messageSpacing : 0;
        double width = double.IsInfinity(availW) ? maxW : availW;
        return new Size(width, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var slot in _slots)
            // Arrange across the full width; the user bubble (HorizontalAlignment.Right) positions itself.
            slot.Element?.Arrange(new Rect(0, slot.Y, finalSize.Width, slot.Height));
        return finalSize;
    }

    // --- Test hooks ---

    internal int RealizedCountForTest => _slots.Count(s => s.View is not null);

    internal IReadOnlyList<string> SelectableTextsForTest() => _selection.SelectableTexts();

    internal string SelectAcrossAndGetMarkdownForTest(int segA, int offA, int segB, int offB) =>
        _selection.SelectAndGetMarkdown(segA, offA, segB, offB);

    internal void FlushActiveForTest() => ActiveSlot()?.View?.FlushForTest();

    internal IReadOnlyList<string> ActionLabelsForTest(int index) =>
        _slots[index].ActionBar is StackPanel bar
            ? bar.Children.OfType<Button>().Select(b => (string)b.Content).ToList()
            : Array.Empty<string>();

    internal void InvokeActionForTest(int index, string label)
    {
        if (_slots[index].ActionBar is not StackPanel bar)
            return;
        foreach (var button in bar.Children.OfType<Button>())
            if ((string)button.Content == label)
            {
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return;
            }
    }

    internal string MessageTextForTest(int index) => _slots[index].View?.GetAccessibleText() ?? string.Empty;

    internal string AccessibleTextForTest()
    {
        var sb = new StringBuilder();
        foreach (var slot in _slots)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(slot.Role).Append(": ").Append(slot.Markdown.ToString().TrimEnd());
        }
        return sb.ToString();
    }
}
