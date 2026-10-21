using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Luna.Controls;

/// <summary>
/// 播放横向排列的精灵图动画；每一帧按行列从整张图片中裁切。
/// </summary>
public sealed class SpriteAnimation : Image
{
    public static readonly DependencyProperty SpriteSourceProperty = DependencyProperty.Register(
        nameof(SpriteSource), typeof(string), typeof(SpriteAnimation),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty FrameWidthProperty = DependencyProperty.Register(
        nameof(FrameWidth), typeof(int), typeof(SpriteAnimation),
        new PropertyMetadata(0, OnVisualPropertyChanged));

    public static readonly DependencyProperty FrameHeightProperty = DependencyProperty.Register(
        nameof(FrameHeight), typeof(int), typeof(SpriteAnimation),
        new PropertyMetadata(0, OnVisualPropertyChanged));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(SpriteAnimation),
        new PropertyMetadata(0, OnVisualPropertyChanged));

    public static readonly DependencyProperty FrameCountProperty = DependencyProperty.Register(
        nameof(FrameCount), typeof(int), typeof(SpriteAnimation),
        new PropertyMetadata(0, OnVisualPropertyChanged));

    public static readonly DependencyProperty FrameDurationProperty = DependencyProperty.Register(
        nameof(FrameDuration), typeof(TimeSpan), typeof(SpriteAnimation),
        new PropertyMetadata(TimeSpan.FromMilliseconds(70)));

    private readonly List<BitmapSource> _frames = [];
    private DispatcherTimer? _timer;
    private int _index;

    public SpriteAnimation()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Start();
            else Stop();
        };
    }

    public string? SpriteSource
    {
        get => (string?)GetValue(SpriteSourceProperty);
        set => SetValue(SpriteSourceProperty, value);
    }

    public int FrameWidth
    {
        get => (int)GetValue(FrameWidthProperty);
        set => SetValue(FrameWidthProperty, value);
    }

    public int FrameHeight
    {
        get => (int)GetValue(FrameHeightProperty);
        set => SetValue(FrameHeightProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public int FrameCount
    {
        get => (int)GetValue(FrameCountProperty);
        set => SetValue(FrameCountProperty, value);
    }

    public TimeSpan FrameDuration
    {
        get => (TimeSpan)GetValue(FrameDurationProperty);
        set => SetValue(FrameDurationProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SpriteAnimation)d).Rebuild();

    private void Rebuild()
    {
        Stop();
        _frames.Clear();
        Source = null;
        _index = 0;

        if (string.IsNullOrWhiteSpace(SpriteSource)
            || FrameWidth <= 0 || FrameHeight <= 0
            || Columns <= 0 || FrameCount <= 0)
            return;

        var uri = new Uri($"pack://application:,,,/{SpriteSource.TrimStart('/')}", UriKind.Absolute);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = uri;
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();

        for (var i = 0; i < FrameCount; i++)
        {
            var x = i % Columns * FrameWidth;
            var y = i / Columns * FrameHeight;
            if (x + FrameWidth > bitmap.PixelWidth || y + FrameHeight > bitmap.PixelHeight)
                break;

            var frame = new CroppedBitmap(bitmap, new Int32Rect(x, y, FrameWidth, FrameHeight));
            frame.Freeze();
            _frames.Add(frame);
        }

        if (_frames.Count == 0) return;

        Source = _frames[0];
        if (_frames.Count > 1 && IsVisible)
            Start();
    }

    private void Start()
    {
        if (_timer is not null || _frames.Count < 2) return;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameDuration };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void Stop()
    {
        if (_timer is null) return;

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _index = (_index + 1) % _frames.Count;
        Source = _frames[_index];
    }
}
