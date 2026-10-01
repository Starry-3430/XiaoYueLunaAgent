using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// A self-drawn block image (M2-4). Loads bitmaps or scalable SVG (M3) asynchronously, shows a placeholder
/// while loading and an error box on failure, and scales to fit the available width keeping aspect ratio.
/// Decoded images are cached per URL; bitmaps also use a file-backed cache.
/// </summary>
internal sealed class ImageView : FrameworkElement
{
    private const double PlaceholderHeight = 140;
    private const double PlaceholderWidth = 280;

    private readonly string _url;
    private readonly string _alt;
    private readonly string? _basePath;
    private readonly MarkdownStyle _theme;

    private string _cacheKey = string.Empty;
    private ImageSource? _image;
    private bool _failed;

    public ImageView(ImageBlock block, MarkdownStyle theme, string? basePath = null)
    {
        _url = block.Url;
        _alt = block.Alt;
        _basePath = basePath;
        _theme = theme;
        BeginLoad();
    }

    private void BeginLoad()
    {
        var uri = Resolve(_url, _basePath);
        if (uri is null)
        {
            _failed = true;
            return;
        }
        _cacheKey = uri.ToString();

        if (ImageCache.TryMemory(_cacheKey, out var mem))
        {
            _image = mem;
            return;
        }

        _ = LoadAsync(uri);
    }

    private async Task LoadAsync(Uri uri)
    {
        byte[]? bytes = await ImageLoader.LoadBytesAsync(uri);
        if (bytes is null || bytes.Length == 0)
        {
            OnFailed();
            return;
        }

        // Sniff the content rather than trusting the extension, so SVG served without a .svg URL still works.
        // SVG goes through the optional SVG capability; with no plugin it falls back to the alt-text placeholder.
        ImageSource? image = SvgImage.LooksLikeSvg(bytes)
            ? await Task.Run(() => Capabilities.Svg?.Render(bytes))
            : DecodeBitmap(bytes);

        if (image is null)
            OnFailed();
        else
            OnLoadedImage(image);
    }

    private static BitmapImage? DecodeBitmap(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static Uri? Resolve(string url, string? basePath)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
            return abs;
        if (string.IsNullOrEmpty(basePath))
            return null;
        if (Uri.TryCreate(basePath, UriKind.Absolute, out var baseUri) && baseUri.Scheme is "http" or "https")
            return Uri.TryCreate(baseUri, url, out var combined) ? combined : null;
        try
        {
            return new Uri(Path.GetFullPath(Path.Combine(basePath, url)));
        }
        catch
        {
            return null;
        }
    }

    private void OnLoadedImage(ImageSource image)
    {
        if (_cacheKey.Length > 0)
            ImageCache.PutMemory(_cacheKey, image);
        _image = image;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnFailed()
    {
        _failed = true;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxW = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        if (_image is { Width: > 0, Height: > 0 })
        {
            double w = Math.Min(maxW, _image.Width);
            double h = _image.Height * (w / _image.Width);
            return new Size(w, h);
        }
        return new Size(Math.Min(maxW, PlaceholderWidth), PlaceholderHeight);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        Cursor = _image is not null ? System.Windows.Input.Cursors.Hand : null;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_image is not null)
            e.Handled = true; // suppress document drag-selection over the image
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_image is not null)
        {
            ImageLightbox.Show(_image, this);
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_image is not null)
        {
            // The block is arranged at full content width; draw the image at its own aspect ratio (derived
            // from the measured height), left-aligned, so a narrow image (e.g. an SVG icon) isn't stretched.
            double h = RenderSize.Height;
            double w = _image.Height > 0 ? Math.Min(RenderSize.Width, h * (_image.Width / _image.Height)) : RenderSize.Width;
            dc.DrawImage(_image, new Rect(0, 0, w, h));
            return;
        }

        dc.DrawRoundedRectangle(_theme.CodeBlockBackground, new Pen(_theme.Border, 1),
            new Rect(0, 0, RenderSize.Width, RenderSize.Height), 6, 6);

        double dpi;
        try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { dpi = 1.0; }
        string label = _failed ? $"⚠ 图片加载失败 {_alt}" : $"加载图片… {_alt}";
        var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            _theme.BaseTypeface, _theme.EmSize - 1, _theme.Foreground, dpi)
        {
            MaxTextWidth = Math.Max(1, RenderSize.Width - 20),
            MaxLineCount = 3,
        };
        dc.DrawText(ft, new Point(10, 10));
    }
}
