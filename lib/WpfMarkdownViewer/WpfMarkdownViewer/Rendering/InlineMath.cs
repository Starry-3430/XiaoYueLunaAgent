using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// Caches inline-math vector <see cref="Geometry"/> produced by the registered <see cref="IMathRenderer"/>
/// (<see cref="Capabilities.Math"/>). Dependency-free — the math plugin does the actual LaTeX parsing; the
/// geometry is colourless (filled with the theme brush at draw time) so it's safe to cache by latex + scale.
/// </summary>
internal static class InlineMath
{
    private static readonly ConcurrentDictionary<(string Latex, double Scale), (Geometry? Geometry, Rect Bounds)> Cache = new();

    /// <summary>Render <paramref name="latex"/> to geometry whose baseline sits at y = 0. False if no math plugin is registered or it fails.</summary>
    public static bool TryBuild(string latex, double scale, out Geometry geometry, out Rect bounds)
    {
        var (g, b) = Cache.GetOrAdd((latex, scale), key => Render(key.Latex, key.Scale));
        geometry = g!;
        bounds = b;
        return g is not null;
    }

    private static (Geometry?, Rect) Render(string latex, double scale) =>
        Capabilities.Math is { } math && math.TryRender(latex, scale, display: false, out var g, out var b)
            ? (g, b)
            : (null, Rect.Empty);
}
