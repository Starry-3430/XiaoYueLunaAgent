using System.Text;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// SVG detection helpers that stay in the dependency-free core. Actual SVG decoding is an optional
/// capability (<see cref="ISvgRenderer"/>); with no plugin installed, sniffed SVG bytes fall back to
/// the alt-text placeholder. These checks only classify bytes/URIs and pull in no third-party code.
/// </summary>
internal static class SvgImage
{
    /// <summary>Extension hint (the authoritative check is <see cref="LooksLikeSvg"/> on the fetched bytes).</summary>
    public static bool IsSvg(Uri uri) =>
        uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sniff whether bytes are SVG (XML with an &lt;svg&gt; root), independent of URL extension.</summary>
    public static bool LooksLikeSvg(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 512);
        string head = Encoding.UTF8.GetString(bytes, 0, n);
        int svg = head.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (svg < 0)
            return false;
        int xml = head.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        return xml < 0 || xml <= svg; // only the XML prolog (if any) may precede <svg
    }
}
