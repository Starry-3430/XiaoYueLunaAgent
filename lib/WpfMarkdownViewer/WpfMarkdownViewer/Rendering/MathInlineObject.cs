using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace WpfMarkdownViewer.Rendering;

/// <summary>
/// An inline math formula embedded in a paragraph's text line (ADR-0005 + ADR-0012). It occupies the
/// Visible-Space characters of its LaTeX source but draws as one vector formula.
/// </summary>
/// <remarks>
/// WpfMath's geometry is positioned with its top-left near the origin and does not expose the formula's
/// own baseline through the public API. So rather than baseline-align, we center the formula's ink box on
/// the surrounding text's math axis (≈ a quarter em above the baseline) — the same way browsers/TeX place
/// inline math — which only needs the well-defined geometry bounds.
/// </remarks>
internal sealed class MathInlineObject : TextEmbeddedObject
{
    private readonly CharacterBufferReference _reference;
    private readonly int _length;
    private readonly TextRunProperties _properties;
    private readonly Geometry _geometry;
    private readonly Rect _bounds;
    private readonly Brush _brush;
    private readonly double _ascent; // distance from the reserved box top down to the text baseline

    public MathInlineObject(string text, int offset, int length, TextRunProperties properties,
        Geometry geometry, Rect bounds, Brush brush, double axisHeight)
    {
        _reference = new CharacterBufferReference(text, offset);
        _length = length;
        _properties = properties;
        _geometry = geometry;
        _bounds = bounds;
        _brush = brush;
        _ascent = Math.Min(axisHeight + bounds.Height / 2, bounds.Height);
    }

    public override CharacterBufferReference CharacterBufferReference => _reference;
    public override int Length => _length;
    public override TextRunProperties Properties => _properties;

    public override LineBreakCondition BreakBefore => LineBreakCondition.BreakDesired;
    public override LineBreakCondition BreakAfter => LineBreakCondition.BreakDesired;
    public override bool HasFixedSize => true;

    public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) =>
        new(_bounds.Width, _bounds.Height, _ascent);

    public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) =>
        new(0, -_ascent, _bounds.Width, _bounds.Height);

    public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways)
    {
        // origin is on the text baseline at the run's left edge; place the box top _ascent above it.
        drawingContext.PushTransform(new TranslateTransform(origin.X - _bounds.Left, origin.Y - _ascent - _bounds.Top));
        drawingContext.DrawGeometry(_brush, null, _geometry);
        drawingContext.Pop();
    }
}
