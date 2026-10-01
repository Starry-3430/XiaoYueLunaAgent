using System.Globalization;
using System.Text.RegularExpressions;

namespace WpfMarkdownViewer.Mermaid;

/// <summary>
/// Flattens the CSS custom properties Mermaider emits (<c>var(--…)</c>, <c>color-mix(…)</c>, <c>rem</c> font
/// sizes, a <c>&lt;style&gt;</c> block) into concrete inline values, because native SVG renderers like
/// SharpVectors don't resolve modern CSS. The diagram body only ever uses simple <c>var(--name)</c>, so we
/// evaluate the <c>&lt;style&gt;</c> variable definitions once, substitute them in, and drop the style block.
/// </summary>
internal static partial class MermaidSvgFlattener
{
    public static string Flatten(string svg)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (RootStyle().Match(svg) is { Success: true } root)
            ParseDeclarations(root.Groups[1].Value, raw);
        if (StyleBlock().Match(svg) is { Success: true } style && SvgRule().Match(style.Groups[1].Value) is { Success: true } rule)
            ParseDeclarations(rule.Groups[1].Value, raw);

        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in raw.Keys)
            resolved[name] = Eval(raw[name], raw, cache);

        svg = StyleBlock().Replace(svg, string.Empty);
        return VarRef().Replace(svg, m =>
            resolved.TryGetValue(m.Groups[1].Value, out var v) ? v : "none");
    }

    private static void ParseDeclarations(string text, Dictionary<string, string> into)
    {
        foreach (var decl in text.Split(';'))
        {
            int colon = decl.IndexOf(':');
            if (colon <= 0)
                continue;
            string name = decl[..colon].Trim();
            if (name.StartsWith("--"))
                into[name] = decl[(colon + 1)..].Trim();
        }
    }

    // Resolve a CSS value to a concrete color / number, following var() and color-mix() recursively.
    private static string Eval(string expr, Dictionary<string, string> vars, Dictionary<string, string> cache)
    {
        expr = expr.Trim();
        if (cache.TryGetValue(expr, out var hit))
            return hit;
        cache[expr] = "#000000"; // break any accidental cycle

        string result;
        if (expr.StartsWith("var(", StringComparison.OrdinalIgnoreCase))
        {
            var args = SplitTopLevel(Inner(expr));
            string name = args[0].Trim();
            result = vars.TryGetValue(name, out var inner) ? Eval(inner, vars, cache)
                : args.Count > 1 ? Eval(args[1], vars, cache)
                : "#000000";
        }
        else if (expr.StartsWith("color-mix(", StringComparison.OrdinalIgnoreCase))
        {
            var args = SplitTopLevel(Inner(expr)); // "in srgb", "A p%", "B [q%]"
            var (ca, pa) = ColorAndPercent(args.Count > 1 ? args[1] : string.Empty);
            var (cb, pb) = ColorAndPercent(args.Count > 2 ? args[2] : string.Empty);
            if (pa < 0 && pb < 0) (pa, pb) = (50, 50);
            else if (pa < 0) pa = 100 - pb;
            else if (pb < 0) pb = 100 - pa;
            result = Blend(Eval(ca, vars, cache), pa, Eval(cb, vars, cache), pb);
        }
        else if (expr.EndsWith("rem", StringComparison.OrdinalIgnoreCase)
                 && double.TryParse(expr[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out double rem))
        {
            result = (rem * 16).ToString("0.##", CultureInfo.InvariantCulture);
        }
        else
        {
            result = expr; // a literal color / number
        }

        cache[expr] = result;
        return result;
    }

    private static (string Color, double Percent) ColorAndPercent(string part)
    {
        part = part.Trim();
        int sp = part.LastIndexOf(' ');
        if (sp > 0 && part.EndsWith("%") &&
            double.TryParse(part[(sp + 1)..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
            return (part[..sp].Trim(), p);
        return (part, -1);
    }

    private static string Blend(string hexA, double wa, string hexB, double wb)
    {
        if (!TryColor(hexA, out var a) || !TryColor(hexB, out var b))
            return hexA;
        double total = wa + wb <= 0 ? 1 : wa + wb;
        int Mix(int x, int y) => (int)Math.Round((x * wa + y * wb) / total);
        return $"#{Mix(a.R, b.R):X2}{Mix(a.G, b.G):X2}{Mix(a.B, b.B):X2}";
    }

    private static bool TryColor(string hex, out (int R, int G, int B) rgb)
    {
        rgb = default;
        hex = hex.Trim();
        if (!hex.StartsWith('#'))
            return false;
        hex = hex[1..];
        if (hex.Length == 3)
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length < 6
            || !int.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, null, out int r)
            || !int.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, null, out int g)
            || !int.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, null, out int b))
            return false;
        rgb = (r, g, b);
        return true;
    }

    // Text between the first '(' and its matching ')'.
    private static string Inner(string expr)
    {
        int open = expr.IndexOf('(');
        int depth = 0;
        for (int i = open; i < expr.Length; i++)
        {
            if (expr[i] == '(') depth++;
            else if (expr[i] == ')' && --depth == 0)
                return expr[(open + 1)..i];
        }
        return expr[(open + 1)..];
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    [GeneratedRegex(@"<svg\b[^>]*\bstyle=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex RootStyle();

    [GeneratedRegex(@"<style\b[^>]*>(.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlock();

    [GeneratedRegex(@"\bsvg\s*\{([^}]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex SvgRule();

    [GeneratedRegex(@"var\(\s*(--[\w-]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex VarRef();
}
