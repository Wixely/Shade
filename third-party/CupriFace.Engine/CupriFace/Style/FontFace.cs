using System.Text;

namespace CupriFace.Style;

/// <summary>One <c>src</c> entry of an <c>@font-face</c>: a URL and its optional <c>format()</c> hint.</summary>
public sealed record FontFaceSource(string Url, string? Format);

/// <summary>
/// A parsed <c>@font-face</c> rule. <see cref="WeightMin"/>–<see cref="WeightMax"/> is the declared
/// <c>font-weight</c> (a single value gives an equal pair; a range <c>300 700</c> spans it), and
/// <see cref="Slant"/> the declared <c>font-style</c>. The declared values are what the CSS cascade
/// matches against, so they override whatever the font file says about itself — a file whose
/// internal name is "MyFont-Bold" is registered as <c>MyFont</c> at 700 if the rule says so.
/// </summary>
public sealed record FontFaceRule(string Family, IReadOnlyList<FontFaceSource> Sources, int WeightMin, int WeightMax, FontSlant Slant);

/// <summary>
/// Extracts <c>@font-face</c> blocks from a stylesheet. The rule parser skips every at-rule but
/// <c>@media</c>, so these are read here, the way <c>@keyframes</c> are read by
/// <see cref="Animation"/>. Only <c>url()</c> sources are kept: a <c>local()</c> source names a
/// platform font, which is exactly what an app registering its own faces is trying not to depend on.
/// </summary>
public static class FontFace
{
    public static List<FontFaceRule> Parse(string? css)
    {
        var rules = new List<FontFaceRule>();
        if (string.IsNullOrEmpty(css)) return rules;

        var i = 0;
        while ((i = css.IndexOf("@font-face", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var open = css.IndexOf('{', i);
            if (open < 0) break;
            var close = MatchBrace(css, open);
            if (close < 0) break;
            var body = css[(open + 1)..close];
            i = close + 1;

            string? family = null;
            var sources = new List<FontFaceSource>();
            int min = 400, max = 400;
            var slant = FontSlant.Normal;

            foreach (var (name, value) in Declarations(body))
            {
                switch (name)
                {
                    case "font-family":
                        family = Unquote(value);
                        break;
                    case "src":
                        foreach (var entry in SplitTopLevel(value, ','))
                        {
                            var url = Function(entry, "url");
                            if (url is null) continue;              // local() — a platform font, not ours
                            sources.Add(new FontFaceSource(Unquote(url), Function(entry, "format") is { } f ? Unquote(f) : null));
                        }
                        break;
                    case "font-weight":
                        (min, max) = ParseWeightRange(value);
                        break;
                    case "font-style":
                        slant = value.Trim().ToLowerInvariant() switch
                        {
                            "italic" => FontSlant.Italic,
                            "oblique" => FontSlant.Oblique,
                            _ => FontSlant.Normal,
                        };
                        break;
                }
            }

            if (family is { Length: > 0 } && sources.Count > 0)
                rules.Add(new FontFaceRule(family, sources, min, max, slant));
        }
        return rules;
    }

    /// <summary><c>400</c>, <c>bold</c>, <c>normal</c>, or a range <c>300 700</c>.</summary>
    internal static (int Min, int Max) ParseWeightRange(string value)
    {
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var a = ParseWeight(parts.Length > 0 ? parts[0] : "400");
        var b = parts.Length > 1 ? ParseWeight(parts[1]) : a;
        return a <= b ? (a, b) : (b, a);
    }

    private static int ParseWeight(string token) => token.ToLowerInvariant() switch
    {
        "normal" => 400,
        "bold" => 700,
        _ => int.TryParse(token, out var w) ? Math.Clamp(w, 1, 1000) : 400,
    };

    // Declarations split on top-level ';' only: a data: URL carries ';base64' inside url(), and
    // the generic declaration splitter would cut it there.
    private static IEnumerable<(string Name, string Value)> Declarations(string body)
    {
        foreach (var decl in SplitTopLevel(body, ';'))
        {
            var colon = decl.IndexOf(':');
            if (colon <= 0) continue;
            yield return (decl[..colon].Trim().ToLowerInvariant(), decl[(colon + 1)..].Trim());
        }
    }

    private static IEnumerable<string> SplitTopLevel(string s, char sep)
    {
        var depth = 0; var quote = '\0'; var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == sep && depth == 0)
            {
                var part = s[start..i].Trim();
                if (part.Length > 0) yield return part;
                start = i + 1;
            }
        }
        var last = s[start..].Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>The argument of <c>name(…)</c> in <paramref name="s"/>, or null if absent.</summary>
    private static string? Function(string s, string name)
    {
        var at = s.IndexOf(name + "(", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var open = at + name.Length;
        var depth = 0; var quote = '\0';
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return s[(open + 1)..i].Trim();
        }
        return null;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] is '"' or '\'') && s[^1] == s[0] ? s[1..^1] : s;
    }

    private static int MatchBrace(string s, int open)
    {
        var depth = 0; var quote = '\0';
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
