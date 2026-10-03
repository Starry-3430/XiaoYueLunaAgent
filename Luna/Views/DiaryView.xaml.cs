using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Luna.Models;
using Luna.ViewModels;

namespace Luna.Views;

/// <summary>
/// 横向分页日记浏览器：卡片沿 X 轴排列，当前日居中完整显示，相邻日缩小并露出一部分。
/// 通过底部按钮翻页（带惯性吸附）；±2 的卡片叠加渐变遮罩，过渡更自然。
/// </summary>
public partial class DiaryView : UserControl
{
    private const double CardWidth = 320;
    private const double CardHeight = 460;
    private const double ContentWidth = 250;
    private const double ContentMaxHeight = 170; // 约 8 行
    private const double Spacing = 310;      // 相邻卡片中心间距（拉开一点）
    private const double MaskInner = 600;    // 遮罩内边界距中心的像素（保持原值不变）
    private const double NeighborScale = 0.82;

    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF0));
    private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33));
    private static readonly Brush SubtleBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x7B, 0x6B));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0x32, 0x26));
    private static readonly Brush BeigeBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE0, 0xCA));

    private static readonly ObservableCollection<DiaryDayView> EmptyDays = new();

    private readonly Dictionary<int, Border> _visuals = new();
    private bool _hooked;
    private bool _relayoutQueued;
    private double _lastMaskWidth = -1;

    private double _position = double.NaN;
    private double _target;
    private double _velocity;
    private long _lastFrame;
    private EventHandler? _renderHandler;

    public DiaryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Loaded += (_, _) => { Hook(); Layout(); };
    }

    private DiaryViewModel? Vm => DataContext as DiaryViewModel;
    private ObservableCollection<DiaryDayView> Days => Vm?.Days ?? EmptyDays;

    private void Hook()
    {
        if (_hooked || Vm is null) return;
        _hooked = true;
        Vm.Days.CollectionChanged += OnDaysChanged;
    }

    private void OnDaysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_relayoutQueued) return;
        _relayoutQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _relayoutQueued = false;
            ClearVisuals();
            if (Days.Count == 0)
            {
                _position = double.NaN;
                return;
            }
            if (double.IsNaN(_position))
            {
                _position = _target = Days.Count - 1; // 默认定位到最新（今天）
            }
            else
            {
                _position = Math.Clamp(_position, 0, Days.Count - 1);
                _target = Math.Round(_position);
            }
            Layout();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ClearVisuals()
    {
        foreach (var v in _visuals.Values)
            PagerCanvas.Children.Remove(v);
        _visuals.Clear();
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    /// <summary>
    /// 左右遮罩：从“±2 卡片中点”（距中心 2*Spacing）起，向内 150px 渐隐到背景色，
    /// 再向外一直保持背景色直到界面边缘。这样最多只露出 ±2 的一半，且更外的卡片不会闪现。
    /// </summary>
    private void UpdateMasks(double hostW)
    {
        var width = Math.Max(0, hostW / 2 - MaskInner);
        if (Math.Abs(width - _lastMaskWidth) < 0.5) return;
        _lastMaskWidth = width;

        if (width <= 0)
        {
            LeftFade.Visibility = Visibility.Collapsed;
            RightFade.Visibility = Visibility.Collapsed;
            return;
        }

        LeftFade.Visibility = Visibility.Visible;
        RightFade.Visibility = Visibility.Visible;
        LeftFade.Width = width;
        RightFade.Width = width;

        var bg = Color.FromArgb(0xFF, 0xE4, 0xE0, 0xCA);
        var clear = Color.FromArgb(0x00, 0xE4, 0xE0, 0xCA);
        var fade = Math.Min(1.0, 150.0 / width); // 150px 过渡带

        LeftFade.Background = new LinearGradientBrush(
            new GradientStopCollection { new(bg, 0), new(bg, 1 - fade), new(clear, 1) },
            new Point(0, 0.5), new Point(1, 0.5));
        RightFade.Background = new LinearGradientBrush(
            new GradientStopCollection { new(clear, 0), new(bg, fade), new(bg, 1) },
            new Point(0, 0.5), new Point(1, 0.5));
    }

    // ===== 布局 =====

    private void Layout()
    {
        var hostW = PagerHost.ActualWidth;
        var hostH = PagerHost.ActualHeight;
        if (hostW <= 0 || hostH <= 0 || Days.Count == 0 || double.IsNaN(_position))
            return;

        var center = hostW / 2;
        var top = Math.Max(0, (hostH - CardHeight) / 2);

        UpdateMasks(hostW);

        var lo = Math.Max(0, (int)Math.Floor(_position) - 2);
        var hi = Math.Min(Days.Count - 1, (int)Math.Ceiling(_position) + 2);

        foreach (var kv in _visuals)
        {
            if (kv.Key < lo || kv.Key > hi)
                kv.Value.Visibility = Visibility.Collapsed;
        }

        for (var i = lo; i <= hi; i++)
        {
            var card = GetOrCreate(i);
            card.Visibility = Visibility.Visible;

            var delta = i - _position;
            Canvas.SetLeft(card, center - CardWidth / 2 + delta * Spacing);
            Canvas.SetTop(card, top);

            var t = Math.Min(Math.Abs(delta), 1);
            var scale = 1 - t * (1 - NeighborScale);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = new ScaleTransform(scale, scale);
            card.Opacity = 1 - t * 0.25;
            Panel.SetZIndex(card, 100 - (int)Math.Round(Math.Abs(delta) * 10));
        }
    }

    private Border GetOrCreate(int index)
    {
        if (_visuals.TryGetValue(index, out var existing))
            return existing;

        var card = BuildCard(Days[index]);
        _visuals[index] = card;
        PagerCanvas.Children.Add(card);
        return card;
    }

    private Border BuildCard(DiaryDayView day)
    {
        var border = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            Background = CardBrush,
            CornerRadius = new CornerRadius(14),
        };

        var root = new Grid();

        var contentGrid = new Grid { Margin = new Thickness(18) };
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var date = new TextBlock
        {
            Text = day.LogicalDate,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = TitleBrush,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(date, 0);
        contentGrid.Children.Add(date);

        var content = BuildContent(day);
        Grid.SetRow(content, 1);
        contentGrid.Children.Add(content);

        root.Children.Add(contentGrid);
        border.Child = root;
        return border;
    }

    private FrameworkElement BuildContent(DiaryDayView day)
    {
        if (day.HasContent)
        {
            var text = new TextBlock
            {
                Text = day.Content,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 15,
                LineHeight = 20,
                Foreground = TitleBrush,
            };
            return new ScrollViewer
            {
                Width = ContentWidth,
                MaxHeight = ContentMaxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Content = text,
            };
        }

        if (day.IsFailed)
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(new TextBlock
            {
                Text = "生成失败！",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = ErrorBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            });
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(day.LastError) ? "未知错误" : day.LastError,
                FontSize = 12,
                Foreground = SubtleBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = ContentWidth,
                Margin = new Thickness(0, 0, 0, 12),
            });

            var retry = new Button
            {
                Content = "重试",
                Tag = day,
                Background = BeigeBrush,
                Foreground = TitleBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(18, 6, 18, 6),
                FontSize = 13,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            retry.Click += Retry_Click;
            panel.Children.Add(retry);
            return panel;
        }

        var nickname = Vm?.Nickname ?? "Luna";
        var message = day.IsPending
            ? $"{nickname}还在记录\n明天再来看看吧！"
            : $"这一天没有和\n{nickname}互动过哦！";

        return new TextBlock
        {
            Text = message,
            FontSize = 13,
            LineHeight = 22,
            Foreground = SubtleBrush,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DiaryDayView day } && Vm is not null)
            await Vm.RetryAsync(day);
    }

    // ===== 翻页按钮 =====

    private void Prev_Click(object sender, RoutedEventArgs e) => MoveBy(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => MoveBy(1);

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        if (Days.Count == 0) return;
        GotoIndex(Days.Count - 1);
    }

    private void MoveBy(int delta)
    {
        if (Days.Count == 0 || double.IsNaN(_position)) return;
        GotoIndex((int)Math.Round(_position) + delta);
    }

    private void GotoIndex(int index)
    {
        _target = ClampPos(index);
        _velocity = 0;
        if (double.IsNaN(_position)) _position = _target;
        StartAnim();
    }

    private double ClampPos(double p) => Math.Clamp(p, 0, Math.Max(0, Days.Count - 1));

    private void StartAnim()
    {
        if (_renderHandler is not null) return;
        _lastFrame = Stopwatch.GetTimestamp();
        _renderHandler = (_, _) => OnFrame();
        CompositionTarget.Rendering += _renderHandler;
    }

    private void StopAnim()
    {
        if (_renderHandler is null) return;
        CompositionTarget.Rendering -= _renderHandler;
        _renderHandler = null;
    }

    private void OnFrame()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastFrame) / (double)Stopwatch.Frequency;
        _lastFrame = now;
        if (dt <= 0) return;
        if (dt > 0.05) dt = 0.05;

        // 临界阻尼弹簧：起步快、中途平滑、接近目标减速并吸附
        const double stiffness = 180;
        const double damping = 26;
        var accel = (_target - _position) * stiffness - _velocity * damping;
        _velocity += accel * dt;
        _position += _velocity * dt;

        if (Math.Abs(_target - _position) < 0.001 && Math.Abs(_velocity) < 0.01)
        {
            _position = _target;
            _velocity = 0;
            Layout();
            StopAnim();
            return;
        }

        Layout();
    }

    // ===== 搜索 =====

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is null) return;
        e.Handled = true;

        var index = await Vm.SearchAsync();
        if (index is not int i) return;

        StopAnim();
        if (double.IsNaN(_position)) _position = i;
        _velocity = 0;
        _target = i;
        StartAnim();
    }
}
