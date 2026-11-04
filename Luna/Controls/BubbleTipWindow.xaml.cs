using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Luna.Controls;

/// <summary>气泡指针所在的边。Tail 在该边上，并指向目标。</summary>
public enum BubbleSide
{
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>
/// 带尖角指针的米色气泡提示窗口。指针与气泡主体在同一个窗口中，
/// 窗口位置会持续跟随目标控件或屏幕坐标，目标移动、宿主窗口移动/缩放时指针始终指向目标。
/// </summary>
public partial class BubbleTipWindow : Window
{
    private const double ShadowPadding = 14;
    private const double TailDepth = 9;
    private const double TailHalfWidth = 7;
    private const double CornerInset = 12;
    private const double MaxBodyWidth = 200;

    private readonly FrameworkElement? _target;
    private readonly Point _targetPoint;
    private readonly BubbleSide _side;
    private readonly DispatcherTimer _trackTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(120),
    };

    private Window? _ownerWindow;
    private DispatcherTimer? _durationTimer;
    private TimeSpan? _maxDisplayDuration;
    private bool _ready;
    private bool _updating;
    private bool _wasActivated;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    public BubbleTipWindow(string message, FrameworkElement target, BubbleSide side)
    {
        InitializeComponent();
        _target = target;
        _side = side;
        _ownerWindow = Window.GetWindow(target);
        MessageText.Text = message;
        Initialize();
    }

    public BubbleTipWindow(string message, Point targetScreenPoint, BubbleSide side)
    {
        InitializeComponent();
        _targetPoint = targetScreenPoint;
        _side = side;
        MessageText.Text = message;
        Initialize();
    }

    /// <summary>显示气泡，并在指定时长后自动关闭。</summary>
    public void ShowFor(TimeSpan maxDisplayDuration)
    {
        _maxDisplayDuration = maxDisplayDuration;
        Show();
    }

    /// <summary>更新气泡文字，并重新计算尺寸和位置。</summary>
    public void SetMessage(string message)
    {
        MessageText.Text = message;
        ResizeBody();
        UpdatePosition();
    }

    private void Initialize()
    {
        if (_ownerWindow is not null)
        {
            Owner = _ownerWindow;
            _ownerWindow.Deactivated += OnOwnerDeactivated;
        }

        Topmost = _ownerWindow is null;
        Activated += OnBubbleActivated;
        Deactivated += OnBubbleDeactivated;

        ResizeBody();

        if (_target is not null)
        {
            _target.LayoutUpdated += OnAnchorChanged;
            _target.IsVisibleChanged += OnTargetVisibilityChanged;
            _target.Unloaded += OnTargetUnloaded;
        }

        if (_ownerWindow is not null)
        {
            _ownerWindow.LocationChanged += OnAnchorChanged;
            _ownerWindow.SizeChanged += OnAnchorChanged;
            _ownerWindow.StateChanged += OnAnchorChanged;
            _ownerWindow.Closed += OnOwnerClosed;
        }

        _trackTimer.Tick += OnAnchorChanged;

        Loaded += (_, _) =>
        {
            _ready = true;
            UpdatePosition();
            _trackTimer.Start();
            StartDurationTimer();
        };

        Closing += (_, _) =>
        {
            _ready = false;
            _trackTimer.Stop();
            _durationTimer?.Stop();
            DetachEvents();
        };

        Closed += (_, _) =>
        {
            _trackTimer.Stop();
            _durationTimer?.Stop();
        };
    }

    private void StartDurationTimer()
    {
        if (_maxDisplayDuration is not { } max || max <= TimeSpan.Zero) return;

        _durationTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = max };
        _durationTimer.Tick += (_, _) =>
        {
            _durationTimer?.Stop();
            Close();
        };
        _durationTimer.Start();
    }

    private void OnTargetVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e) => UpdatePosition();

    private void OnTargetUnloaded(object? sender, RoutedEventArgs e) => Close();

    private void OnOwnerClosed(object? sender, EventArgs e) => Close();

    private void OnOwnerDeactivated(object? sender, EventArgs e) => Close();

    private void OnBubbleActivated(object? sender, EventArgs e) => _wasActivated = true;

    private void OnBubbleDeactivated(object? sender, EventArgs e)
    {
        if (_wasActivated) Close();
    }

    private void CloseButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void DetachEvents()
    {
        Activated -= OnBubbleActivated;
        Deactivated -= OnBubbleDeactivated;

        if (_target is not null)
        {
            _target.LayoutUpdated -= OnAnchorChanged;
            _target.IsVisibleChanged -= OnTargetVisibilityChanged;
            _target.Unloaded -= OnTargetUnloaded;
        }

        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated -= OnOwnerDeactivated;
            _ownerWindow.LocationChanged -= OnAnchorChanged;
            _ownerWindow.SizeChanged -= OnAnchorChanged;
            _ownerWindow.StateChanged -= OnAnchorChanged;
            _ownerWindow.Closed -= OnOwnerClosed;
        }

        _trackTimer.Tick -= OnAnchorChanged;
    }

    private void ResizeBody()
    {
        Body.Width = double.NaN;
        Body.Height = double.NaN;
        Body.Measure(new Size(MaxBodyWidth, double.PositiveInfinity));

        var width = Math.Ceiling(Math.Min(MaxBodyWidth, Body.DesiredSize.Width));
        var height = Math.Ceiling(Body.DesiredSize.Height);

        Body.Width = width;
        Body.Height = height;

        if (_side is BubbleSide.Top or BubbleSide.Bottom)
        {
            Width = width + ShadowPadding * 2;
            Height = height + TailDepth + ShadowPadding * 2;
        }
        else
        {
            Width = width + TailDepth + ShadowPadding * 2;
            Height = height + ShadowPadding * 2;
        }
    }

    private void OnAnchorChanged(object? sender, EventArgs e) => UpdatePosition();

    private void UpdatePosition()
    {
        if (!_ready || _updating) return;

        if (_target is not null
            && (!_target.IsVisible
                || PresentationSource.FromVisual(_target) is null
                || _ownerWindow?.WindowState == WindowState.Minimized))
        {
            Close();
            return;
        }

        _updating = true;
        try
        {
            ApplyLayout();
        }
        finally
        {
            _updating = false;
        }
    }

    private void ApplyLayout()
    {
        var tip = _target is not null ? GetAnchorPoint(_target) : _targetPoint;
        if (double.IsNaN(tip.X) || double.IsNaN(tip.Y)) return;

        var bodyWidth = Body.Width;
        var bodyHeight = Body.Height;
        var pad = ShadowPadding;
        var tail = TailDepth;

        double windowLeft;
        double windowTop;

        switch (_side)
        {
            case BubbleSide.Bottom:
                windowLeft = tip.X - bodyWidth / 2 - pad;
                windowTop = tip.Y - tail - bodyHeight - pad;
                break;
            case BubbleSide.Top:
                windowLeft = tip.X - bodyWidth / 2 - pad;
                windowTop = tip.Y - pad;
                break;
            case BubbleSide.Left:
                windowLeft = tip.X - pad;
                windowTop = tip.Y - bodyHeight / 2 - pad;
                break;
            default:
                windowLeft = tip.X - tail - bodyWidth - pad;
                windowTop = tip.Y - bodyHeight / 2 - pad;
                break;
        }

        var work = GetWorkArea(tip);
        windowLeft = Clamp(windowLeft, work.Left, Math.Max(work.Left, work.Right - Width));
        windowTop = Clamp(windowTop, work.Top, Math.Max(work.Top, work.Bottom - Height));

        Left = windowLeft;
        Top = windowTop;

        var bodyX = _side == BubbleSide.Left ? pad + tail : pad;
        var bodyY = _side == BubbleSide.Top ? pad + tail : pad;

        Canvas.SetLeft(Body, bodyX);
        Canvas.SetTop(Body, bodyY);

        UpdateTail(bodyX, bodyY, bodyWidth, bodyHeight, tip.X - windowLeft, tip.Y - windowTop);
    }

    private Point GetAnchorPoint(FrameworkElement target)
    {
        var topLeftPx = target.PointToScreen(new Point(0, 0));
        var bottomRightPx = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));

        var dpi = VisualTreeHelper.GetDpi(target);
        var topLeft = new Point(topLeftPx.X / dpi.DpiScaleX, topLeftPx.Y / dpi.DpiScaleY);
        var bottomRight = new Point(bottomRightPx.X / dpi.DpiScaleX, bottomRightPx.Y / dpi.DpiScaleY);

        var midX = (topLeft.X + bottomRight.X) / 2;
        var midY = (topLeft.Y + bottomRight.Y) / 2;

        return _side switch
        {
            BubbleSide.Bottom => new Point(midX, topLeft.Y),
            BubbleSide.Top => new Point(midX, bottomRight.Y),
            BubbleSide.Left => new Point(bottomRight.X, midY),
            _ => new Point(topLeft.X, midY),
        };
    }

    private Rect GetWorkArea(Point dipPoint)
    {
        var dpi = _target is not null ? VisualTreeHelper.GetDpi(_target) : VisualTreeHelper.GetDpi(this);
        var nativePoint = new NativePoint
        {
            X = (int)Math.Round(dipPoint.X * dpi.DpiScaleX),
            Y = (int)Math.Round(dipPoint.Y * dpi.DpiScaleY),
        };

        var monitor = MonitorFromPoint(nativePoint, 2);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };

        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var left = info.rcWork.Left / dpi.DpiScaleX;
            var top = info.rcWork.Top / dpi.DpiScaleY;
            var right = info.rcWork.Right / dpi.DpiScaleX;
            var bottom = info.rcWork.Bottom / dpi.DpiScaleY;
            return new Rect(left, top, right - left, bottom - top);
        }

        return SystemParameters.WorkArea;
    }

    private void UpdateTail(double bodyX, double bodyY, double bodyWidth, double bodyHeight,
        double tipX, double tipY)
    {
        var half = TailHalfWidth;
        var minX = bodyX + CornerInset + half;
        var maxX = bodyX + bodyWidth - CornerInset - half;
        var minY = bodyY + CornerInset + half;
        var maxY = bodyY + bodyHeight - CornerInset - half;

        if (maxX < minX) minX = maxX = bodyX + bodyWidth / 2;
        if (maxY < minY) minY = maxY = bodyY + bodyHeight / 2;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            switch (_side)
            {
                case BubbleSide.Bottom:
                {
                    var baseX = Clamp(tipX, minX, maxX);
                    var baseY = bodyY + bodyHeight;
                    ctx.BeginFigure(new Point(baseX - half, baseY), true, true);
                    ctx.LineTo(new Point(baseX + half, baseY), true, false);
                    ctx.LineTo(new Point(tipX, baseY + TailDepth), true, false);
                    break;
                }
                case BubbleSide.Top:
                {
                    var baseX = Clamp(tipX, minX, maxX);
                    var baseY = bodyY;
                    ctx.BeginFigure(new Point(baseX - half, baseY), true, true);
                    ctx.LineTo(new Point(baseX + half, baseY), true, false);
                    ctx.LineTo(new Point(tipX, baseY - TailDepth), true, false);
                    break;
                }
                case BubbleSide.Left:
                {
                    var baseY = Clamp(tipY, minY, maxY);
                    var baseX = bodyX;
                    ctx.BeginFigure(new Point(baseX, baseY - half), true, true);
                    ctx.LineTo(new Point(baseX, baseY + half), true, false);
                    ctx.LineTo(new Point(baseX - TailDepth, tipY), true, false);
                    break;
                }
                default:
                {
                    var baseY = Clamp(tipY, minY, maxY);
                    var baseX = bodyX + bodyWidth;
                    ctx.BeginFigure(new Point(baseX, baseY - half), true, true);
                    ctx.LineTo(new Point(baseX, baseY + half), true, false);
                    ctx.LineTo(new Point(baseX + TailDepth, tipY), true, false);
                    break;
                }
            }
        }

        geometry.Freeze();
        Tail.Data = geometry;
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}
