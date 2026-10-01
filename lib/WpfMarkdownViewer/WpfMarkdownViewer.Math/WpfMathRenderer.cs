using System.Windows;
using System.Windows.Media;
using WpfMarkdownViewer.Rendering;
using WpfMath.Parsers;
using WpfMath.Rendering;

namespace WpfMarkdownViewer.MathRendering;

/// <summary>WpfMath implementation of <see cref="IMathRenderer"/> (ADR-0012): LaTeX → colourless vector geometry with the baseline at y = 0.</summary>
public sealed class WpfMathRenderer : IMathRenderer
{
    private static readonly XamlMath.TexFormulaParser Parser = WpfTeXFormulaParser.Instance;

    public bool TryRender(string latex, double emSize, bool display, out Geometry geometry, out Rect bounds)
    {
        geometry = Geometry.Empty;
        bounds = Rect.Empty;
        try
        {
            var formula = Parser.Parse(latex);
            var environment = WpfTeXEnvironment.Create(
                display ? XamlMath.TexStyle.Display : XamlMath.TexStyle.Text, emSize, "Arial", Brushes.Black, Brushes.Transparent);
            var g = formula.RenderToGeometry(environment, emSize, 0, 0);
            g.Freeze();
            var b = g.Bounds;
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0)
                return false;
            geometry = g;
            bounds = b;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
