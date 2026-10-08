using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WpfMarkdownViewer.Model;
using WpfMarkdownViewer.Rendering;
using WpfMarkdownViewer.Streaming;

namespace WpfMarkdownViewer.Controls;

/// <summary>Raised when the user activates a link. The component never navigates itself (security: AI-generated content).</summary>
public sealed class LinkClickedEventArgs : EventArgs
{
    public string Url { get; }

    public LinkClickedEventArgs(string url) => Url = url;
}

/// <summary>
/// The core single-Document Markdown renderer (CONTEXT.md: "Document"). Read-only (ADR-0009); self-drawn
/// blocks (ADR-0005). It is non-scrolling content that stacks Block visuals top-to-bottom; the Scroll
/// Host (phase E) wraps it. Finalized Block visuals are immutable and reused; compatible Active Block
/// visuals are updated in place while streaming.
/// </summary>
/// <remarks>
/// <see cref="AppendDelta"/> is safe to call from any thread; everything else is expected on the UI
/// thread. A background <see cref="DispatcherTimer"/> drains the queue on an adaptive cadence
/// ("自适应离散三档") and re-derives + re-renders the Document.
/// <para>Timer lifecycle: the flush pump runs only while appended deltas are waiting to be flushed and stops
/// as soon as the queue is drained; the streaming caret blinks only while it is shown and the control is
/// visible. <c>Unloaded</c> stops every timer (pending deltas are kept) and <c>Loaded</c> resumes whatever is
/// still needed, so the control can enter and leave the tree repeatedly and never keeps itself (or its
/// window) alive through the Dispatcher once it is idle or unloaded. No explicit disposal is required.</para>
/// </remarks>
public class MarkdownDocumentView : Panel, IVirtualizingContent, IScrollHostAware
{
    private const double VirtualizationBuffer = 500;
    private const double EstimatedBlockHeight = 40;

    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly StringBuilder _source = new();
    private readonly StreamingBlockParser _parser = new();
    private readonly AdaptiveThrottlePolicy _policy = new();
    private readonly DispatcherTimer _pump;

    private MarkdownStyle _theme = MarkdownStyle.Light;

    private long _tokensSeen;
    private long _tokensAtLastTick;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private DateTime _lastInputUtc = DateTime.UtcNow;
    private bool _completed;
    private bool _dirty;

    /// <summary>1 while the pump is running or a start has been queued (AppendDelta may run off the UI thread).</summary>
    private int _pumpRequested;

    /// <summary>True between <c>Unloaded</c> and the next <c>Loaded</c>: no timer may run.</summary>
    private bool _suspended;

    /// <summary>How many leading slots correspond to finalized, immutable Blocks (never rebuilt).</summary>
    private int _stableCount;

    private readonly List<BlockSlot> _slots = new();
    private double _viewportTop;
    private double _viewportHeight; // 0 ⇒ no Scroll Host connected ⇒ realize all (no virtualization)

    private sealed class BlockSlot
    {
        public required MdBlock Block { get; set; }
        public FrameworkElement? View { get; set; }
        public double Height { get; set; }
        public double Y { get; set; }
        public bool Finalized { get; set; }
    }

    /// <summary>When false, all Blocks stay realized (no virtualization) — e.g. for printing/snapshots. Default true.</summary>
    public bool VirtualizationEnabled { get; set; } = true;

    /// <summary>When true, the measured width shrinks to the content (capped at the available width) instead of filling it — used for user chat bubbles (M3). Default false.</summary>
    public bool ShrinkToContentWidth { get; set; }

    /// <summary>Base path or URI for resolving relative image URLs (M2-4). Null ⇒ only absolute URLs load.</summary>
    public string? ImageBasePath { get; set; }

    void IVirtualizingContent.SetViewport(double top, double height)
    {
        _viewportTop = top;
        _viewportHeight = height;
        InvalidateMeasure();
    }

    void IScrollHostAware.AttachScroll(Action<double> scrollByVertical) =>
        _selection.EnableAutoScroll(() => (_viewportTop, _viewportHeight), scrollByVertical);

    internal int SlotCountForTest => _slots.Count;
    internal int RealizedCountForTest => _slots.Count(s => s.View is not null);

    public MarkdownDocumentView()
    {
        Background = _theme.Background;
        Focusable = true;
        _selection = new SelectionController(this);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection()));
        _pump = new DispatcherTimer(DispatcherPriority.Input) { Interval = _policy.MidInterval };
        _pump.Tick += OnPumpTick;
        _caretBlink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretBlink.Tick += OnCaretBlink;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateCaretTimer();
        BuildContextMenu();
    }

    /// <summary>Whether the streaming flush timer is currently running (diagnostics). False once pending deltas are flushed or the control is unloaded.</summary>
    public bool IsFlushTimerRunning => _pump.IsEnabled;

    /// <summary>Whether the streaming caret blink timer is currently running (diagnostics). True only while a streaming caret is shown and the control is visible.</summary>
    public bool IsCaretTimerRunning => _caretBlink.IsEnabled;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _suspended = false;
        if (!_incoming.IsEmpty || _dirty)
            RequestPump();
        UpdateCaretTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Pending deltas stay queued and are flushed after the next Loaded.
        _suspended = true;
        _pump.Stop();
        Interlocked.Exchange(ref _pumpRequested, 0);
        UpdateCaretTimer();
        if (_selection.IsDragging)
            _selection.End();
    }

    /// <summary>Ensure the pump will run. Thread-safe.</summary>
    private void RequestPump()
    {
        if (Interlocked.Exchange(ref _pumpRequested, 1) == 1)
            return;
        if (Dispatcher.CheckAccess())
            StartPump();
        else
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartPump));
    }

    private void StartPump()
    {
        if (_suspended)
        {
            Interlocked.Exchange(ref _pumpRequested, 0);
            return;
        }
        if (_pump.IsEnabled)
            return;
        // A restart after an idle gap resumes at the cadence the always-on pump used while idle.
        if (_policy.IsIdle(DateTime.UtcNow - _lastTickUtc))
            _pump.Interval = _policy.MidInterval;
        _pump.Start();
    }

    private void StopPump()
    {
        _pump.Stop();
        Interlocked.Exchange(ref _pumpRequested, 0);
        // A producer may have enqueued after the drain but before the flag was cleared.
        if (!_incoming.IsEmpty)
            RequestPump();
    }

    /// <summary>The appearance configuration (fonts, sizes, margins, colors). Settable in code or XAML; runtime-swappable (M2-1).</summary>
    public static readonly DependencyProperty MarkdownStyleProperty = DependencyProperty.Register(
        nameof(MarkdownStyle), typeof(MarkdownStyle), typeof(MarkdownDocumentView),
        new PropertyMetadata(MarkdownStyle.Light, OnMarkdownStyleChanged));

    public MarkdownStyle MarkdownStyle
    {
        get => (MarkdownStyle)GetValue(MarkdownStyleProperty);
        set => SetValue(MarkdownStyleProperty, value);
    }

    private static void OnMarkdownStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownDocumentView)d;
        view._theme = (MarkdownStyle)e.NewValue;
        view.Background = view._theme.Background;
        view.RestyleRealizedBlocks();
    }

    /// <summary>
    /// Re-create every realized Block visual with the current style. Block visuals bake the style in at
    /// construction (colors, fonts, em sizes, highlighted code colors), so the in-place streaming update
    /// (<see cref="BlockViewFactory.TryUpdate"/>) cannot restyle them. The parsed Blocks are reused as-is:
    /// no re-parse, no change to the source buffer, the pending delta queue, the flush pump or the
    /// finalized/active split — a stream in progress simply continues with the new style.
    /// Virtualized-away slots have no visual and pick the style up when they are realized again.
    /// </summary>
    private void RestyleRealizedBlocks()
    {
        foreach (var slot in _slots)
        {
            if (slot.View is not { } oldView)
                continue;
            var replacement = BlockViewFactory.Create(slot.Block, _theme, RaiseLink, ImageBasePath, Document.LinkDefinitions);
            int visualIndex = InternalChildren.IndexOf(oldView);
            InternalChildren.Remove(oldView);
            if (visualIndex >= 0)
                InternalChildren.Insert(visualIndex, replacement);
            else
                InternalChildren.Add(replacement);
            slot.View = replacement;
            slot.Height = 0; // font metrics may have changed; re-measure
        }

        // The streaming caret rides the trailing visual, which was just replaced.
        UpdateCaret();
        InvalidateMeasure();
    }

    /// <summary>Convenience: switch the appearance at runtime (routes through the MarkdownStyle property).</summary>
    public void ApplyTheme(MarkdownStyle theme) => MarkdownStyle = theme ?? throw new ArgumentNullException(nameof(theme));

    /// <summary>The parsed Document. Exposed for tests and tooling.</summary>
    internal Document Document => _parser.Document;

    /// <summary>Raised when the user activates a link. The host decides whether/how to navigate.</summary>
    public event EventHandler<LinkClickedEventArgs>? LinkClicked;

    /// <summary>Raised on the UI thread after a flush changes the Document.</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>Append a streamed token/delta. Thread-safe: may be called from any thread.</summary>
    public void AppendDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;
        _incoming.Enqueue(delta);
        Interlocked.Add(ref _tokensSeen, delta.Length);
        _lastInputUtc = DateTime.UtcNow;
        RequestPump();
    }

    /// <summary>Signal that the stream is complete; finalizes the Active Block (Markdig becomes authoritative).</summary>
    public void Complete()
    {
        _completed = true;
        _dirty = true;
        Flush();
    }

    /// <summary>Clear all state so the Document can be re-streamed (e.g. "regenerate response").</summary>
    public void Reset()
    {
        while (_incoming.TryDequeue(out _)) { }
        if (_pump.IsEnabled)
            StopPump();
        _source.Clear();
        _completed = false;
        Interlocked.Exchange(ref _tokensSeen, 0);
        _tokensAtLastTick = 0;
        _stableCount = 0;
        _parser.Reparse(string.Empty, streamComplete: false);
        Render();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Best-effort finalize a cancelled stream; stops sticky-follow and expects no more tokens.</summary>
    public void Abort()
    {
        _completed = true;
        _dirty = true;
        Flush();
        // Phase E: stop sticky-bottom following.
    }

    /// <summary>Render already-complete Markdown with no token stream (static path).</summary>
    public void SetMarkdown(string markdown)
    {
        Reset();
        AppendDelta(markdown);
        Complete();
    }

    private void OnPumpTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        bool idle = _policy.IsIdle(now - _lastInputUtc);

        Flush();

        double elapsedSec = Math.Max((now - _lastTickUtc).TotalSeconds, 1e-3);
        long current = Interlocked.Read(ref _tokensSeen);
        double rate = (current - _tokensAtLastTick) / elapsedSec;
        _tokensAtLastTick = current;
        _lastTickUtc = now;

        _pump.Interval = idle ? _policy.MidInterval : _policy.NextInterval(rate);

        // Nothing left to flush: stop instead of spinning. The next AppendDelta restarts the pump.
        if (_incoming.IsEmpty && !_dirty)
            StopPump();
    }

    /// <summary>Drain the queue into the source buffer, re-derive the Document, and re-render. UI thread only.</summary>
    private void Flush()
    {
        bool changed = _dirty;
        _dirty = false;
        while (_incoming.TryDequeue(out var delta))
        {
            _source.Append(delta);
            changed = true;
        }
        // Drained synchronously (Complete/Abort/SetMarkdown/pump tick): the pump has nothing left to do.
        if (_pump.IsEnabled && _incoming.IsEmpty)
            StopPump();
        if (!changed)
            return;

        // Normalize \(…\) / \[…\] to $…$ / $$…$$ so math works regardless of which delimiter the model emits.
        string text = MathDelimiters.Normalize(_source.ToString());

        // Streaming → best-effort preview; on completion → Markdig is authoritative (ADR-0002).
        if (_completed)
            _parser.FinalizeFromMarkdig(text);
        else
            _parser.Reparse(text, streamComplete: false);

        Render();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reconcile slots with the Document, updating compatible streaming tail visuals in place.</summary>
    private void Render()
    {
        var blocks = Document.Blocks;
        if (_stableCount > blocks.Count || _stableCount > _slots.Count)
        {
            InternalChildren.Clear();
            _slots.Clear();
            _stableCount = 0;
        }

        int sharedCount = Math.Min(_slots.Count, blocks.Count);
        for (int i = _stableCount; i < sharedCount; i++)
        {
            var slot = _slots[i];
            var block = blocks[i];
            bool sameShape = BlockShape.Of(slot.Block) == BlockShape.Of(block);
            bool updated = sameShape
                           && slot.View is { } existing
                           && BlockViewFactory.TryUpdate(existing, block, Document.LinkDefinitions);
            slot.Block = block;
            if (!updated && slot.View is { } oldView)
            {
                int visualIndex = InternalChildren.IndexOf(oldView);
                var replacement = BlockViewFactory.Create(
                    block,
                    _theme,
                    RaiseLink,
                    ImageBasePath,
                    Document.LinkDefinitions);
                InternalChildren.Remove(oldView);
                if (visualIndex >= 0)
                    InternalChildren.Insert(visualIndex, replacement);
                else
                    InternalChildren.Add(replacement);
                slot.View = replacement;
                slot.Height = 0;
            }
        }

        for (int i = _slots.Count - 1; i >= blocks.Count; i--)
        {
            if (_slots[i].View is { } view)
                InternalChildren.Remove(view);
            _slots.RemoveAt(i);
        }

        for (int i = _slots.Count; i < blocks.Count; i++)
        {
            var view = BlockViewFactory.Create(blocks[i], _theme, RaiseLink, ImageBasePath, Document.LinkDefinitions);
            InternalChildren.Add(view);
            _slots.Add(new BlockSlot { Block = blocks[i], View = view });
        }

        int stable = 0;
        while (stable < blocks.Count && blocks[stable].IsFinalized)
            stable++;
        _stableCount = stable;
        for (int i = 0; i < _slots.Count; i++)
            _slots[i].Finalized = i < _stableCount;

        UpdateCaret();
        InvalidateMeasure();
    }

    // --- Streaming "typing" caret on the active block (ChatGPT-style) ---

    private readonly DispatcherTimer _caretBlink;
    private bool _caretOn = true;
    private ParagraphView? _caretView;

    private void UpdateCaret()
    {
        // The caret rides the trailing block while streaming, but only for text blocks (paragraph/heading/list item).
        var active = !_completed && _slots.Count > 0 ? _slots[^1].View as ParagraphView : null;
        if (!ReferenceEquals(active, _caretView))
        {
            if (_caretView is not null)
            {
                _caretView.ShowCaret = false;
                _caretView.InvalidateVisual();
            }
            _caretView = active;
            _caretOn = true;
        }

        if (_caretView is not null)
            _caretView.ShowCaret = _caretOn;
        UpdateCaretTimer();
    }

    /// <summary>Blink only while a streaming caret is shown and can actually be seen (visible ⇒ loaded in a shown window).</summary>
    private void UpdateCaretTimer()
    {
        bool want = _caretView is not null && !_suspended && IsVisible;
        if (want)
        {
            if (!_caretBlink.IsEnabled)
                _caretBlink.Start();
            return;
        }

        if (!_caretBlink.IsEnabled)
            return;
        _caretBlink.Stop();
        // Leave a steady caret behind so an unseen/snapshot render still shows it; blinking resumes when visible again.
        if (_caretView is not null && !_caretOn)
        {
            _caretOn = true;
            _caretView.ShowCaret = true;
            _caretView.InvalidateVisual();
        }
    }

    private void OnCaretBlink(object? sender, EventArgs e)
    {
        _caretOn = !_caretOn;
        if (_caretView is not null)
        {
            _caretView.ShowCaret = _caretOn;
            _caretView.InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var pad = _theme.ContentPadding;
        double spacing = _theme.BlockSpacing;
        double availW = availableSize.Width;
        double contentW = Math.Max(1, (double.IsInfinity(availW) ? 800 : availW) - pad.Left - pad.Right);

        bool virtualize = VirtualizationEnabled && _viewportHeight > 0;
        double bufTop = _viewportTop - VirtualizationBuffer;
        double bufBottom = _viewportTop + _viewportHeight + VirtualizationBuffer;

        // Pass 1: provisional Y from cached/estimated heights, then realize/devirtualize per viewport.
        double y = pad.Top;
        foreach (var slot in _slots)
        {
            slot.Y = y;
            y += (slot.Height > 0 ? slot.Height : EstimatedBlockHeight) + spacing;
        }
        foreach (var slot in _slots)
        {
            bool onScreen = !virtualize || !slot.Finalized || slot.Height <= 0
                || (slot.Y <= bufBottom && slot.Y + Math.Max(slot.Height, EstimatedBlockHeight) >= bufTop);
            if (onScreen && slot.View is null)
            {
                slot.View = BlockViewFactory.Create(slot.Block, _theme, RaiseLink, ImageBasePath, Document.LinkDefinitions);
                InternalChildren.Add(slot.View);
            }
            else if (!onScreen && slot.View is { } v && slot.Finalized)
            {
                InternalChildren.Remove(v);
                slot.View = null;
            }
        }

        // Pass 2: measure realized slots, cache their heights, compute final layout.
        y = pad.Top;
        double maxW = 0;
        foreach (var slot in _slots)
        {
            if (slot.View is { } v)
            {
                v.Measure(new Size(contentW, double.PositiveInfinity));
                slot.Height = v.DesiredSize.Height;
                maxW = Math.Max(maxW, v.DesiredSize.Width);
            }
            else if (slot.Height <= 0)
            {
                slot.Height = EstimatedBlockHeight;
            }
            slot.Y = y;
            y += slot.Height + spacing;
        }

        double contentBottom = _slots.Count > 0 ? y - spacing : pad.Top;
        double natural = maxW + pad.Left + pad.Right;
        double width = double.IsInfinity(availW) ? natural
            : ShrinkToContentWidth ? Math.Min(availW, natural)
            : availW;

        return new Size(width, contentBottom + pad.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var pad = _theme.ContentPadding;
        double w = Math.Max(0, finalSize.Width - pad.Left - pad.Right);
        foreach (var slot in _slots)
            slot.View?.Arrange(new Rect(pad.Left, slot.Y, w, slot.View.DesiredSize.Height));
        return finalSize;
    }

    /// <summary>Drive a single synchronous flush. Test/host hook so streaming can be advanced deterministically.</summary>
    internal void FlushForTest() => Flush();

    /// <summary>The whole Document as accessible read-only plain text (ADR-0009). Also the basis for plain-text copy.</summary>
    internal string GetAccessibleText() => DocumentTextSerializer.ToPlainText(Document);

    protected override AutomationPeer OnCreateAutomationPeer() => new MarkdownDocumentAutomationPeer(this);

    private void RaiseLink(string url) => OnLinkClicked(url);

    // --- Selection (ADR-0008): document-level drag-select, delegated to the shared SelectionController.
    // Disabled when hosted in a Conversation Shell, which runs one controller spanning all messages. ---

    private readonly SelectionController _selection;

    /// <summary>When false the control ignores drag-selection (the host owns it, e.g. cross-message selection in the shell). Default true.</summary>
    public bool SelectionEnabled { get; set; } = true;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || !SelectionEnabled) // a child handled it (link/copy button), or the host owns selection
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
        // Capture taken away mid-drag (Alt+Tab, window closed, another control captured): end the drag so the
        // edge auto-scroll timer does not keep running without a matching mouse-up.
        if (_selection.IsDragging)
            _selection.End();
    }

    /// <summary>Select all text and draw the highlight.</summary>
    public void SelectAll() => _selection.SelectAll();

    /// <summary>Copy the current selection as plain-text Markdown only (the text IS the Markdown source, so it round-trips into any editor).</summary>
    public void CopySelection() => _selection.Copy();

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

    // --- Test hooks ---

    internal IReadOnlyList<string> SelectableTextsForTest() => _selection.SelectableTexts();
    internal bool IsAutoScrollTimerRunningForTest => _selection.IsAutoScrollTimerRunning;
    internal bool BeginDragForTest(Point point) => _selection.Begin(point);
    internal string SelectAndGetTextForTest(int segA, int offA, int segB, int offB) => _selection.SelectAndGetText(segA, offA, segB, offB);
    internal string SelectAndGetMarkdownForTest(int segA, int offA, int segB, int offB) => _selection.SelectAndGetMarkdown(segA, offA, segB, offB);
    internal string SelectAndGetHtmlForTest(int segA, int offA, int segB, int offB) => _selection.SelectAndGetHtml(segA, offA, segB, offB);

    protected virtual void OnLinkClicked(string url) =>
        LinkClicked?.Invoke(this, new LinkClickedEventArgs(url));
}
