using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Luna.Services;

/// <summary>
/// 图片附件压缩：等比缩放到最长边 1920，按是否真正含透明像素选择 PNG/JPEG，
/// 并在编码前处理 EXIF 方向，避免模型看到旋转错误的图片。
/// </summary>
public static class ImageCompressionHelper
{
    public const int MaxDimension = 1920;
    public const int JpegQuality = 80;
    public const long MaxResultSizeBytes = 10L * 1024 * 1024;

    /// <summary>
    /// 读取原图并压缩。成功返回编码后的字节；失败返回 null，并给出技术错误信息。
    /// <paramref name="formatName"/> 为 "PNG" 或 "JPEG"。
    /// </summary>
    public static byte[]? TryCompressImage(string filePath, out string formatName, out string? error)
    {
        formatName = string.Empty;
        error = null;

        try
        {
            var bitmap = LoadFirstFrame(filePath);
            var orientation = GetExifOrientation(filePath);
            var transformed = ApplyExifRotation(bitmap, orientation);
            var resized = Resize(transformed);

            var hasTransparency = HasTransparency(resized);
            formatName = hasTransparency ? "PNG" : "JPEG";

            BitmapEncoder encoder = hasTransparency
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = JpegQuality };

            encoder.Frames.Add(BitmapFrame.Create(resized));

            using var stream = new MemoryStream();
            encoder.Save(stream);

            if (stream.Length > MaxResultSizeBytes)
            {
                error = $"compressed image is {stream.Length} bytes";
                return null;
            }

            return stream.ToArray();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static BitmapSource LoadFirstFrame(string filePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(Path.GetFullPath(filePath), UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static ushort GetExifOrientation(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand);

            if (decoder.Frames.Count > 0 && decoder.Frames[0].Metadata is BitmapMetadata metadata &&
                metadata.ContainsQuery("System.Photo.Orientation"))
            {
                return Convert.ToUInt16(metadata.GetQuery("System.Photo.Orientation"));
            }
        }
        catch
        {
            // WebP 等格式可能没有 WIC 解码器；按无旋转处理。
        }

        return 1;
    }

    private static BitmapSource ApplyExifRotation(BitmapSource bitmap, ushort orientation)
    {
        if (orientation <= 1 || orientation > 8)
            return bitmap;

        Transform transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),   // 水平翻转
            3 => new RotateTransform(180),    // 旋转 180°
            4 => new ScaleTransform(1, -1),   // 垂直翻转
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),     // 顺时针 90°
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),    // 逆时针 90°
            _ => Transform.Identity,
        };

        var result = new TransformedBitmap(bitmap, transform);
        result.Freeze();
        return result;
    }

    private static BitmapSource Resize(BitmapSource bitmap)
    {
        var longest = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (longest <= MaxDimension)
            return bitmap;

        var scale = (double)MaxDimension / longest;
        var resized = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    /// <summary>检查缩放后的图片是否真的含透明像素；全不透明的带 alpha 图仍走 JPEG。</summary>
    private static bool HasTransparency(BitmapSource bitmap)
    {
        var format = bitmap.Format;
        var mayHaveAlpha = format == PixelFormats.Bgra32
                           || format == PixelFormats.Pbgra32
                           || format == PixelFormats.Rgba64
                           || format == PixelFormats.Prgba64
                           || format == PixelFormats.Indexed8
                           || format == PixelFormats.Indexed4
                           || format == PixelFormats.Indexed2
                           || format == PixelFormats.Indexed1;

        if (!mayHaveAlpha)
            return false;

        BitmapSource source = format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
                return true;
        }

        return false;
    }
}
