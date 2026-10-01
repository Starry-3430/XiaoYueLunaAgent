using System.Text.RegularExpressions;

namespace WpfMarkdownViewer.Streaming;

/// <summary>
/// Normalizes LaTeX-style math delimiters that many models emit — <c>\(…\)</c> and <c>\[…\]</c> — into the
/// <c>$…$</c> / <c>$$…$$</c> form the rest of the pipeline already understands, so block detection, both
/// inline projectors, and Converge need no changes. Only matched pairs are converted (an unclosed opener
/// stays literal until it closes), and fenced code blocks are left untouched so code containing <c>\(</c>
/// isn't corrupted.
/// </summary>
public static partial class MathDelimiters
{
    public static string Normalize(string text)
    {
        if (text.IndexOf('\\') < 0) // no backslash ⇒ no LaTeX delimiters; skip the work
            return text;

        // Split on ``` fences: even segments are outside code, odd segments are inside a fenced block.
        var parts = text.Split("```");
        for (int i = 0; i < parts.Length; i += 2)
            parts[i] = Convert(parts[i]);
        return string.Join("```", parts);
    }

    private static string Convert(string s)
    {
        if (s.IndexOf('\\') < 0)
            return s;
        s = DisplayMath().Replace(s, m => "$$" + m.Groups[1].Value + "$$");
        s = InlineMath().Replace(s, m => "$" + m.Groups[1].Value + "$");
        return s;
    }

    // \[ … \] (display) and \( … \) (inline); Singleline so display math may span lines.
    [GeneratedRegex(@"\\\[(.+?)\\\]", RegexOptions.Singleline)]
    private static partial Regex DisplayMath();

    [GeneratedRegex(@"\\\((.+?)\\\)", RegexOptions.Singleline)]
    private static partial Regex InlineMath();
}
