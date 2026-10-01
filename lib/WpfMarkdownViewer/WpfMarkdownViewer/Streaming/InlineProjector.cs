using System.Text;
using WpfMarkdownViewer.Model;

namespace WpfMarkdownViewer.Streaming;

/// <summary>
/// Projects a Block's inline source into a flat list of <see cref="InlineRun"/>s in Visible Space
/// (ADR-0007). This is the streaming, best-effort projector: it is always optimistic — an opened but
/// not-yet-closed marker (e.g. <c>**bold</c>) hides its marker and applies its style to the remainder,
/// matching the ChatGPT "边吐边变粗" feel. On finalize, <see cref="Parsing.MarkdigInlineProjector"/>
/// is authoritative and the two must Converge for closed input.
/// </summary>
public static class InlineProjector
{
    public static InlineProjection Project(string source, IReadOnlyDictionary<string, string>? linkDefs = null)
    {
        var visible = new StringBuilder();
        var runs = new List<InlineRun>();
        var pending = new StringBuilder();
        var style = InlineStyle.None;

        void Flush()
        {
            if (pending.Length > 0)
            {
                runs.Add(new InlineRun(visible.Length - pending.Length, pending.ToString(), style));
                pending.Clear();
            }
        }

        void AppendChar(char c)
        {
            pending.Append(c);
            visible.Append(c);
        }

        void EmitLink(string text, string url)
        {
            var inner = Project(text, linkDefs);
            int baseOffset = visible.Length;
            foreach (var run in inner.Runs)
                runs.Add(run with { VisibleStart = baseOffset + run.VisibleStart, Style = run.Style | style, LinkTarget = url });
            visible.Append(inner.VisibleText);
        }

        int i = 0;
        int n = source.Length;
        while (i < n)
        {
            char c = source[i];

            if (c is '*' or '_' && Matches(source, i, c, 3))
            {
                Flush();
                style ^= InlineStyle.Bold | InlineStyle.Italic;
                i += 3;
            }
            else if (c is '*' or '_' && Matches(source, i, c, 2))
            {
                Flush();
                style ^= InlineStyle.Bold;
                i += 2;
            }
            else if (c is '*' or '_')
            {
                Flush();
                style ^= InlineStyle.Italic;
                i += 1;
            }
            else if (c == '~' && Matches(source, i, '~', 2))
            {
                Flush();
                style ^= InlineStyle.Strikethrough;
                i += 2;
            }
            else if (c == '=' && Matches(source, i, '=', 2))
            {
                Flush();
                style ^= InlineStyle.Highlight;
                i += 2;
            }
            else if (c == '+' && Matches(source, i, '+', 2))
            {
                Flush();
                style ^= InlineStyle.Underline;
                i += 2;
            }
            else if (c == '~')
            {
                Flush();
                style ^= InlineStyle.Subscript;
                i += 1;
            }
            else if (c == '^')
            {
                Flush();
                style ^= InlineStyle.Superscript;
                i += 1;
            }
            else if (c == '`')
            {
                int close = source.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    string code = source[(i + 1)..close];
                    runs.Add(new InlineRun(visible.Length, code, style | InlineStyle.Code));
                    visible.Append(code);
                    i = close + 1;
                }
                else
                {
                    AppendChar(c);
                    i++;
                }
            }
            else if (c == '$' && TryReadInlineMath(source, i, out string latex, out int afterMath))
            {
                Flush();
                runs.Add(new InlineRun(visible.Length, latex, style | InlineStyle.Math));
                visible.Append(latex);
                i = afterMath;
            }
            else if (c == '<' && InlineHtml.TryRead(source, i, out var tag))
            {
                if (tag.IsBreak)
                {
                    AppendChar('\n'); // <br> ⇒ hard line break
                }
                else
                {
                    Flush();
                    if (tag.Style != InlineStyle.None)
                        style ^= tag.Style; // <b>/<sub>/… toggle; unknown tags drop
                }
                i = tag.Next;
            }
            else if ((c == 'h' || c == 'w' || c == 'H' || c == 'W')
                     && AtUrlBoundary(source, i) && TryReadAutolink(source, i, out string autoUrl, out int autoEnd))
            {
                Flush();
                string shown = source[i..autoEnd];
                runs.Add(new InlineRun(visible.Length, shown, style, autoUrl));
                visible.Append(shown);
                i = autoEnd;
            }
            else if (c == '[' && TryReadLink(source, i, out string text, out string url, out int next))
            {
                Flush();
                EmitLink(text, url);
                i = next;
            }
            else if (c == '[' && linkDefs is not null
                     && TryReadReferenceLink(source, i, linkDefs, out string refText, out string refUrl, out int refNext))
            {
                Flush();
                EmitLink(refText, refUrl);
                i = refNext;
            }
            else
            {
                AppendChar(c);
                i++;
            }
        }

        Flush();
        return new InlineProjection(visible.ToString(), runs);
    }

    private static bool Matches(string s, int i, char c, int count)
    {
        if (i + count > s.Length)
            return false;
        for (int k = 0; k < count; k++)
            if (s[i + k] != c)
                return false;
        // For count<3 ensure it is not the start of a longer run that a higher tier should claim.
        return true;
    }

    /// <summary>
    /// Best-effort inline math <c>$…$</c> (single-dollar). Mirrors Markdig's dollarmath enough to Converge:
    /// the opener is not followed by whitespace or another <c>$</c>; the closer is not preceded by whitespace
    /// and not followed by a digit. Unclosed or rule-failing dollars stay literal.
    /// </summary>
    private static bool TryReadInlineMath(string s, int i, out string latex, out int next)
    {
        latex = string.Empty;
        next = i;
        if (i + 1 >= s.Length || s[i + 1] == '$' || char.IsWhiteSpace(s[i + 1]))
            return false;

        for (int j = i + 1; j < s.Length; j++)
        {
            if (s[j] != '$')
                continue;
            bool closeOk = !char.IsWhiteSpace(s[j - 1]) && (j + 1 >= s.Length || !char.IsDigit(s[j + 1]));
            if (closeOk)
            {
                latex = s[(i + 1)..j];
                next = j + 1;
                return latex.Length > 0;
            }
        }
        return false;
    }

    private static bool AtUrlBoundary(string s, int i) => i == 0 || !char.IsLetterOrDigit(s[i - 1]);

    /// <summary>
    /// GFM-style bare autolink: <c>http(s)://…</c> or <c>www.…</c> up to whitespace/<c>&lt;</c>, trimming trailing
    /// punctuation (unbalanced <c>)</c> included). <c>www.</c> links get an <c>http://</c> target.
    /// </summary>
    private static bool TryReadAutolink(string s, int i, out string url, out int next)
    {
        url = string.Empty;
        next = i;
        bool www = false;
        if (StartsWithAt(s, i, "http://") || StartsWithAt(s, i, "https://")) { }
        else if (StartsWithAt(s, i, "www.")) www = true;
        else return false;

        int schemeEnd = i + (www ? 4 : s[i + 4] == 's' ? 8 : 7);
        int j = schemeEnd;
        while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] is not ('<' or '>'))
            j++;

        while (j > schemeEnd && IsTrailingPunctuation(s[j - 1]))
        {
            if (s[j - 1] == ')' && CountChar(s, i, j, ')') <= CountChar(s, i, j, '('))
                break;
            j--;
        }

        if (j <= schemeEnd)
            return false;

        string shown = s[i..j];
        url = www ? "http://" + shown : shown;
        next = j;
        return true;
    }

    private static bool StartsWithAt(string s, int i, string prefix) =>
        i + prefix.Length <= s.Length && string.Compare(s, i, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsTrailingPunctuation(char c) => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or '"' or '\'';

    private static int CountChar(string s, int start, int end, char c)
    {
        int n = 0;
        for (int k = start; k < end; k++)
            if (s[k] == c) n++;
        return n;
    }

    /// <summary>Reference-style links resolved against the document's definitions: <c>[text][label]</c>, <c>[text][]</c>, <c>[label]</c>.</summary>
    private static bool TryReadReferenceLink(string s, int i, IReadOnlyDictionary<string, string> defs,
        out string text, out string url, out int next)
    {
        text = string.Empty;
        url = string.Empty;
        next = i;
        int close = s.IndexOf(']', i + 1);
        if (close < 0)
            return false;
        string inner = s[(i + 1)..close];

        int after = close + 1;
        if (after < s.Length && s[after] == '[')
        {
            int close2 = s.IndexOf(']', after + 1);
            if (close2 < 0)
                return false;
            string label = s[(after + 1)..close2].Trim();
            if (label.Length == 0)
                label = inner.Trim(); // [text][] collapsed reference
            if (!defs.TryGetValue(label, out string? full))
                return false;
            text = inner;
            url = full;
            next = close2 + 1;
            return true;
        }

        // [label] shortcut reference
        if (!defs.TryGetValue(inner.Trim(), out string? shortUrl))
            return false;
        text = inner;
        url = shortUrl;
        next = close + 1;
        return true;
    }

    private static bool TryReadLink(string s, int i, out string text, out string url, out int next)
    {
        text = string.Empty;
        url = string.Empty;
        next = i;
        int close = s.IndexOf(']', i + 1);
        if (close < 0 || close + 1 >= s.Length || s[close + 1] != '(')
            return false;
        int paren = s.IndexOf(')', close + 2);
        if (paren < 0)
            return false;
        text = s[(i + 1)..close];
        url = s[(close + 2)..paren];
        next = paren + 1;
        return true;
    }
}
