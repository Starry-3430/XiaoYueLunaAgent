using System.Collections.Concurrent;
using TextMateSharp.Grammars;

namespace WpfMarkdownViewer.Highlighting;

/// <summary>
/// TextMate-grammar implementation of <see cref="ICodeHighlighter"/> (ADR-0003). Caches a per-theme
/// tokenizer and maps a theme name (e.g. "DarkPlus") to a TextMate <see cref="ThemeName"/>; unknown names
/// fall back to LightPlus.
/// </summary>
public sealed class TextMateHighlighter : ICodeHighlighter
{
    private static readonly ConcurrentDictionary<string, CodeHighlighter> Cache = new();

    public IReadOnlyList<IReadOnlyList<ColoredSpan>> Highlight(string code, string? language, string theme) =>
        Cache.GetOrAdd(theme, t => new CodeHighlighter(ParseTheme(t))).Highlight(code, language);

    private static ThemeName ParseTheme(string theme) =>
        Enum.TryParse<ThemeName>(theme, ignoreCase: true, out var parsed) ? parsed : ThemeName.LightPlus;
}
