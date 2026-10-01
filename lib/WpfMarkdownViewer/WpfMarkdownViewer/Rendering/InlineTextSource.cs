using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Rendering;

/// <summary>A <see cref="TextSource"/> that feeds a paragraph's flat <see cref="InlineRun"/> list to WPF's TextFormatter.</summary>
internal sealed class InlineTextSource : TextSource
{
    private readonly InlineProjection _projection;
    private readonly MarkdownStyle _theme;
    private readonly double _emSize;
    private readonly FontWeight _baseWeight;
    private readonly bool _monospace;

    public InlineTextSource(InlineProjection projection, MarkdownStyle theme, double emSize, FontWeight baseWeight, bool monospace = false)
    {
        _projection = projection;
        _theme = theme;
        _emSize = emSize;
        _baseWeight = baseWeight;
        _monospace = monospace;
    }

    public override TextRun GetTextRun(int index)
    {
        string text = _projection.VisibleText;
        if (index >= text.Length)
            return new TextEndOfParagraph(1);

        // A hard line break (kept as '\n' by InlineSource) ends the current line.
        if (text[index] == '\n')
            return new TextEndOfLine(1);

        InlineRun run = RunAt(index);

        // Inline math renders as one embedded vector formula spanning its whole run; on parse failure it
        // falls through and renders as monospace text (PropsFor treats Math like Code).
        if (!_monospace && run.Style.HasFlag(InlineStyle.Math) && index == run.VisibleStart
            && InlineMath.TryBuild(run.Text, _emSize, out var geometry, out var bounds))
        {
            // Clean props (no code background) — the formula is drawn directly, not as a styled glyph run.
            var mathProps = new InlineRunProperties(
                new Typeface(_theme.BaseTypeface.FontFamily, FontStyles.Normal, _baseWeight, FontStretches.Normal),
                _emSize, _theme.Foreground, null, null);
            return new MathInlineObject(text, run.VisibleStart, run.Text.Length, mathProps,
                geometry, bounds, _theme.Foreground, axisHeight: _emSize * 0.26);
        }

        // Stop the run before the next hard break so the '\n' is emitted as its own TextEndOfLine.
        int end = run.VisibleEnd;
        int nl = text.IndexOf('\n', index, end - index);
        if (nl >= 0)
            end = nl;
        return new TextCharacters(text, index, end - index, PropsFor(run));
    }

    public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int indexLimit) =>
        new(0, new CultureSpecificCharacterBufferRange(CultureInfo.CurrentCulture, CharacterBufferRange.Empty));

    public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;

    private InlineRun RunAt(int index)
    {
        foreach (var run in _projection.Runs)
            if (index >= run.VisibleStart && index < run.VisibleEnd)
                return run;
        // Fallback (should not happen): an unstyled run covering the rest.
        return new InlineRun(index, _projection.VisibleText[index..], InlineStyle.None);
    }

    private InlineRunProperties PropsFor(InlineRun run)
    {
        if (_monospace)
        {
            var mono = new Typeface(_theme.MonoTypeface.FontFamily, FontStyles.Normal, _baseWeight, FontStretches.Normal);
            return new InlineRunProperties(mono, _emSize, _theme.Foreground, null, null);
        }

        bool code = run.Style.HasFlag(InlineStyle.Code);
        var family = code ? _theme.MonoTypeface : _theme.BaseTypeface;
        var weight = run.Style.HasFlag(InlineStyle.Bold) ? FontWeights.Bold : _baseWeight;
        var style = run.Style.HasFlag(InlineStyle.Italic) ? FontStyles.Italic : FontStyles.Normal;
        var typeface = new Typeface(family.FontFamily, style, weight, FontStretches.Normal);

        Brush fg = run.LinkTarget is not null ? _theme.LinkBrush
            : code ? _theme.CodeForeground
            : _theme.Foreground;
        Brush? bg = run.Style.HasFlag(InlineStyle.Highlight) ? _theme.HighlightBackground
            : code ? _theme.InlineCodeBackground
            : null;

        // Sub/superscript: smaller font plus a tunable vertical shift. WPF's BaselineAlignment.Superscript
        // raises by a fixed, font-derived amount that reads too high here, so instead we keep the run on the
        // baseline and translate the glyphs by a fraction of the base em via a TextEffect (ScriptRise/Drop).
        bool sub = run.Style.HasFlag(InlineStyle.Subscript);
        bool sup = run.Style.HasFlag(InlineStyle.Superscript);
        double em = sub || sup ? _emSize * 0.72 : _emSize;
        TextEffectCollection? effects = sup ? ScriptShift(-_emSize * ScriptRise)
            : sub ? ScriptShift(_emSize * ScriptDrop)
            : null;

        return new InlineRunProperties(typeface, em, fg, bg, BuildDecorations(run), effects);
    }

    // Vertical shift of script runs, as a fraction of the base em. Tuned by eye against the surrounding text.
    private const double ScriptRise = 0.34; // superscript: glyphs raised so the top sits near cap height
    private const double ScriptDrop = 0.12; // subscript: glyphs lowered just below the baseline

    private static TextEffectCollection ScriptShift(double dy)
    {
        var transform = new TranslateTransform(0, dy);
        transform.Freeze();
        var effect = new TextEffect(transform, foreground: null, clip: null, positionStart: 0, positionCount: int.MaxValue);
        effect.Freeze();
        var effects = new TextEffectCollection { effect };
        effects.Freeze();
        return effects;
    }

    private static TextDecorationCollection? BuildDecorations(InlineRun run)
    {
        bool underline = run.LinkTarget is not null || run.Style.HasFlag(InlineStyle.Underline);
        bool strike = run.Style.HasFlag(InlineStyle.Strikethrough);
        if (!underline && !strike)
            return null;

        var decorations = new TextDecorationCollection();
        if (underline)
            decorations.Add(TextDecorations.Underline[0]);
        if (strike)
            decorations.Add(TextDecorations.Strikethrough[0]);
        decorations.Freeze();
        return decorations;
    }
}

internal sealed class InlineRunProperties : TextRunProperties
{
    public InlineRunProperties(Typeface typeface, double emSize, Brush fg, Brush? bg, TextDecorationCollection? decorations, TextEffectCollection? effects = null)
    {
        Typeface = typeface;
        FontRenderingEmSize = emSize;
        ForegroundBrush = fg;
        BackgroundBrush = bg;
        TextDecorations = decorations;
        TextEffects = effects;
    }

    public override Typeface Typeface { get; }
    public override double FontRenderingEmSize { get; }
    public override double FontHintingEmSize => FontRenderingEmSize;
    public override TextDecorationCollection? TextDecorations { get; }
    public override Brush ForegroundBrush { get; }
    public override Brush? BackgroundBrush { get; }
    public override CultureInfo CultureInfo => CultureInfo.CurrentCulture;
    public override TextEffectCollection? TextEffects { get; }
}

internal sealed class InlineParagraphProperties : TextParagraphProperties
{
    public InlineParagraphProperties(TextRunProperties defaultProps, double lineHeight, TextAlignment alignment = TextAlignment.Left)
    {
        DefaultTextRunProperties = defaultProps;
        LineHeight = lineHeight;
        TextAlignment = alignment;
    }

    public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
    public override TextAlignment TextAlignment { get; }
    public override bool FirstLineInParagraph => false;
    public override double LineHeight { get; }
    public override bool AlwaysCollapsible => true;
    public override TextRunProperties DefaultTextRunProperties { get; }
    public override TextWrapping TextWrapping => TextWrapping.Wrap;
    public override TextMarkerProperties? TextMarkerProperties => null;
    public override double Indent => 0;
}
