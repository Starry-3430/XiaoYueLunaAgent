using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Streaming;

/// <summary>
/// Recognizes the inline HTML tags models commonly emit and maps the styling ones to <see cref="InlineStyle"/>.
/// Shared by both inline projectors so they stay Converged. Unknown tags are still "read" (so they can be
/// dropped rather than shown literally) but carry no style.
/// </summary>
public static class InlineHtml
{
    public readonly record struct Tag(bool IsBreak, bool IsClose, InlineStyle Style, int Next);

    /// <summary>Try to read an HTML tag starting at <paramref name="i"/>. False if it isn't a well-formed <c>&lt;…&gt;</c> tag.</summary>
    public static bool TryRead(string s, int i, out Tag tag)
    {
        tag = default;
        if (i >= s.Length || s[i] != '<')
            return false;

        int j = i + 1;
        bool close = false;
        if (j < s.Length && s[j] == '/')
        {
            close = true;
            j++;
        }

        int nameStart = j;
        while (j < s.Length && char.IsLetterOrDigit(s[j]))
            j++;
        if (j == nameStart)
            return false; // "<" not followed by a tag name (e.g. "a < b")

        string name = s[nameStart..j].ToLowerInvariant();
        int gt = s.IndexOf('>', j);
        if (gt < 0)
            return false;

        if (name == "br")
        {
            tag = new Tag(IsBreak: true, close, InlineStyle.None, gt + 1);
            return true;
        }

        var style = name switch
        {
            "b" or "strong" => InlineStyle.Bold,
            "i" or "em" => InlineStyle.Italic,
            "u" or "ins" => InlineStyle.Underline,
            "s" or "del" or "strike" => InlineStyle.Strikethrough,
            "mark" => InlineStyle.Highlight,
            "sub" => InlineStyle.Subscript,
            "sup" => InlineStyle.Superscript,
            "code" or "kbd" => InlineStyle.Code,
            _ => InlineStyle.None, // unknown tag ⇒ read and drop, no styling
        };
        tag = new Tag(IsBreak: false, close, style, gt + 1);
        return true;
    }
}
