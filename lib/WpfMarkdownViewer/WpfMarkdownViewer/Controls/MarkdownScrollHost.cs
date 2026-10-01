using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace WpfMarkdownViewer.Controls;

/// <summary>
/// Owns the single scroll viewport and autoscroll for the (non-scrolling) Document content (ADR-0008,
/// problem 10). Sticky-bottom: follows the bottom while the user is at the bottom; any scroll-up
/// disengages and reveals a "jump to latest" affordance; returning to the bottom re-engages.
/// </summary>
[ContentProperty(nameof(Content))]
public class MarkdownScrollHost : Grid
{
    private const double BottomEpsilon = 2.0;

    private readonly ScrollViewer _scroll;
    private readonly Button _jumpButton;

    private bool _stickToBottom = true;

    public MarkdownScrollHost()
    {
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _scroll.ScrollChanged += OnScrollChanged;
        Children.Add(_scroll);

        _jumpButton = new Button
        {
            Content = "↓ 跳到最新",
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
            Visibility = Visibility.Collapsed,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _jumpButton.Click += (_, _) => JumpToLatest();
        Children.Add(_jumpButton);
    }

    /// <summary>The scrolled content (e.g. a <see cref="MarkdownDocumentView"/>).</summary>
    public object? Content
    {
        get => _scroll.Content;
        set
        {
            _scroll.Content = value;
            // Let the content auto-scroll us while drag-selecting past the viewport edge.
            if (value is IScrollHostAware aware)
                aware.AttachScroll(delta => _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + delta));
        }
    }

    /// <summary>Whether autoscroll is currently following the bottom.</summary>
    public bool IsStickToBottom => _stickToBottom;

    /// <summary>Re-engage following and scroll to the newest content.</summary>
    public void JumpToLatest()
    {
        _stickToBottom = true;
        _scroll.ScrollToVerticalOffset(_scroll.ScrollableHeight);
        _jumpButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Scroll to the top of the content and stop following the bottom.</summary>
    public void ScrollToTop()
    {
        _stickToBottom = false;
        _scroll.ScrollToVerticalOffset(0);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Push the viewport to virtualizing content (ADR-0006) on every scroll/resize.
        if (_scroll.Content is IVirtualizingContent vc)
            vc.SetViewport(_scroll.VerticalOffset, _scroll.ViewportHeight);

        if (e.ExtentHeightChange != 0)
        {
            // Content grew (streaming). Keep pinned to the bottom only if we are following.
            if (_stickToBottom)
                _scroll.ScrollToVerticalOffset(_scroll.ScrollableHeight);
            return;
        }

        // A user (or programmatic) scroll with no content change: update follow state from position.
        bool atBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - BottomEpsilon;
        _stickToBottom = atBottom;
        _jumpButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
    }
}
