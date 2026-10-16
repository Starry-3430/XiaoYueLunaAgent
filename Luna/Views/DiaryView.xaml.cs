using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Luna.Controls;
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
    private const double Spacing = 310;      // 相邻卡片中心间距（拉开一点）
    private const double MaskInner = 600;    // 遮罩内边界距中心的像素（保持原值不变）
    private const double NeighborScale = 0.82;
    private const double IllustrationSize = 120;    // 无正文状态插图的边长
    private const double IllustrationRowHeight = 230; // 插图所在行高（固定，保证插图 y 轴固定且居中偏上）
    private const double PlaceholderTextTop = 28;   // 文本相对下半区的固定上边距（y 轴固定）

    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF0));
    private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x48, 0x33));
    private static readonly Brush SubtleBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x7B, 0x6B));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0x32, 0x26));
    private static readonly Brush BeigeBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE0, 0xCA));

    private static readonly ImageSource PendingIllustration = LoadIllustration("pending");
    private static readonly ImageSource NoDataIllustration = LoadIllustration("nodata");
    private static readonly ImageSource FailedIllustration = LoadIllustration("failed");

    private static ImageSource LoadIllustration(string name)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/Assets/icons/DiaryUI/{name}.png"));
        image.Freeze();
        return image;
    }

    private static readonly ObservableCollection<DiaryDayView> EmptyDays = new();

    private readonly Dictionary<int, Border> _visuals = new();
    private bool _hooked;
    private bool _relayoutQueued;
    private bool _entrancePlayed;
    private double _lastMaskWidth = -1;

    private double _position = double.NaN;
    private double _target;
    private double _velocity;
    private long _lastFrame;
    private EventHandler? _renderHandler;

    public DiaryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { Hook(); SyncDiaryToggle(); };
        Loaded += (_, _) => { Hook(); SyncDiaryToggle(); Layout(); };

        // 页面被切到可见时才播放入场动画（预加载时内容在后台已就绪，隐藏期间尺寸为 0 不会构建卡片）。
        IsVisibleChanged += (_, _) => { if (IsVisible) Layout(); };
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

        PlayEntrance();
    }

    /// <summary>首次显示时给卡片加入场动画：淡入 + 轻微放大回弹。</summary>
    private void PlayEntrance()
    {
        if (_entrancePlayed || _visuals.Count == 0) return;
        _entrancePlayed = true;

        var fade = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        foreach (var wrapper in _visuals.Values)
        {
            // 动画作用在整张卡片（白色背景 + 文本）上，而不是只动内部内容。
            if (wrapper.Visibility != Visibility.Visible) continue;
            if (wrapper.Child is not FrameworkElement card) continue;

            card.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(0.9, 0.9);
            card.RenderTransform = scale;
            card.Opacity = 0;

            card.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = fade });

            var growAnimation = new DoubleAnimation(0.9, 1.0, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = grow,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, growAnimation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, growAnimation);
        }
    }

    private Border GetOrCreate(int index)
    {
        if (_visuals.TryGetValue(index, out var existing))
            return existing;

        // 用一个透明外壳承载布局的定位/缩放/透明度，内部才是真正的卡片（背景 + 文本），
        // 这样入场动画可以整张卡片一起做，而不会被布局每帧写入的变换覆盖。
        var wrapper = new Border { Child = BuildCard(Days[index]) };
        _visuals[index] = wrapper;
        PagerCanvas.Children.Add(wrapper);
        return wrapper;
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
            // 正文铺满整张卡片，只有放不下时才出现滚动条
            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Content = text,
            };
        }

        // Pending / NoData / Failed 三种状态：插图居中偏上、文本居中偏下，二者 y 轴各自固定、x 轴居中。
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(IllustrationRowHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var illustration = BuildIllustration(day);
        Grid.SetRow(illustration, 0);
        root.Children.Add(illustration);

        var placeholderText = BuildPlaceholderText(day);
        Grid.SetRow(placeholderText, 1);
        root.Children.Add(placeholderText);

        return root;
    }

    /// <summary>状态插图：固定大小，横向居中，贴在下半区的顶部之上（即卡片中间偏上位置）。</summary>
    private static FrameworkElement BuildIllustration(DiaryDayView day)
    {
        var source = day.IsFailed ? FailedIllustration
            : day.IsPending || day.IsNotGenerated ? PendingIllustration
            : NoDataIllustration;

        return new Image
        {
            Source = source,
            Width = IllustrationSize,
            Height = IllustrationSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };
    }

    /// <summary>状态文本：从下半区顶部起按固定上边距向下排布，横向居中。</summary>
    private FrameworkElement BuildPlaceholderText(DiaryDayView day)
    {
        if (day.IsFailed)
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, PlaceholderTextTop, 0, 0),
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
                Template = BuildRoundedButtonTemplate(),
            };
            retry.Click += Retry_Click;
            panel.Children.Add(retry);
            return panel;
        }

        // 长时间未整理：显示“还没写”的提示与“补写”按钮
        if (day.IsNotGenerated)
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, PlaceholderTextTop, 0, 0),
            };
            panel.Children.Add(new TextBlock
            {
                Text = "这一天的日记还没写哦！",
                FontSize = 13,
                LineHeight = 22,
                Foreground = SubtleBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = ContentWidth,
                Margin = new Thickness(0, 0, 0, 12),
            });

            var fill = new Button
            {
                Content = "补写",
                Tag = day,
                Background = BeigeBrush,
                Foreground = TitleBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(18, 6, 18, 6),
                FontSize = 13,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center,
                Template = BuildRoundedButtonTemplate(),
            };
            fill.Click += Retry_Click;
            panel.Children.Add(fill);
            return panel;
        }

        var nickname = Vm?.Nickname ?? "Luna";
        var message = day.IsPending
            ? $"{nickname}还在记录\n稍后再来看看吧！"
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
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, PlaceholderTextTop, 0, 0),
        };
    }

    /// <summary>圆角按钮模板（默认模板为直角）。</summary>
    private static ControlTemplate BuildRoundedButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85, "Bg"));
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7, "Bg"));
        template.Triggers.Add(hover);
        template.Triggers.Add(pressed);

        return template;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DiaryDayView day } && Vm is not null)
            await Vm.RetryAsync(day);
    }

    // ===== 日记本开关 =====

    private void SyncDiaryToggle()
    {
        if (Vm is not null)
            DiaryToggle.IsChecked = Vm.DiaryEnabled;
    }

    private void DiaryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || Vm is null) return;

        var wantEnabled = cb.IsChecked == true;

        // 关闭前二次确认；取消则恢复为开启
        if (!wantEnabled && !ConfirmDisableDiary())
        {
            cb.IsChecked = true;
            return;
        }

        Vm.SetDiaryEnabled(wantEnabled);
    }

    private bool ConfirmDisableDiary()
    {
        var confirmed = false;
        var nickname = Vm?.Nickname ?? "Luna";

        var dialog = new LunaDialog
        {
            Owner = Window.GetWindow(this),
            DialogTitle = "提示",
            DialogContent = new TextBlock
            {
                Text = $"关闭日记本，{nickname}将不再整理日记，重新启用日记本后，将会补写最近7天的日记",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
            },
        };

        dialog.Buttons = new ObservableCollection<DialogButton>
        {
            new()
            {
                Text = "关闭",
                StyleKey = "StyleBeige",
                Command = new RelayCommand(() =>
                {
                    confirmed = true;
                    dialog.Close();
                }),
            },
            new()
            {
                Text = "取消",
                StyleKey = "StyleBeige",
                Command = new RelayCommand(dialog.Close),
            },
        };

        dialog.ShowDialog();
        return confirmed;
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
