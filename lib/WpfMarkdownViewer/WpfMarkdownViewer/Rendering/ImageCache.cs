using System.Windows.Media;

namespace WpfMarkdownViewer.Rendering;

/// <summary>Bounded in-memory cache of decoded images (bitmaps and SVG drawings) by resolved URI. Disk caching and revalidation of remote bytes live in <see cref="ImageLoader"/>.</summary>
internal static class ImageCache
{
    private static readonly LruCache<string, ImageSource> Memory = new(capacity: 128);

    public static bool TryMemory(string key, out ImageSource image) => Memory.TryGet(key, out image!);

    public static void PutMemory(string key, ImageSource image) => Memory.Set(key, image);
}
