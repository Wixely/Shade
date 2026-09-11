using System.Text.RegularExpressions;

namespace CupriFace.Style;

/// <summary>A media condition (min/max width and height). Null bounds are unconstrained.
/// Height matters on phones: landscape is WIDE but SHORT, so a width-only breakpoint written for
/// desktop windows would also fire there — `(min-width:…) and (min-height:…)` tells them apart.</summary>
public readonly struct MediaCondition
{
    public readonly float? MinWidth, MaxWidth, MinHeight, MaxHeight;
    public MediaCondition(float? min, float? max, float? minH = null, float? maxH = null)
    { MinWidth = min; MaxWidth = max; MinHeight = minH; MaxHeight = maxH; }
    public bool Matches(float width, float height) =>
        (MinWidth is not { } mn || width >= mn) && (MaxWidth is not { } mx || width <= mx)
        && (MinHeight is not { } mnh || height >= mnh) && (MaxHeight is not { } mxh || height <= mxh);
}

/// <summary>One parsed CSS rule: a single selector plus its declarations.</summary>
public sealed class CssRule
{
    public required string Selector { get; init; }
    public required int Specificity { get; init; }
    public int Order { get; set; }
    public required Dictionary<string, string> Declarations { get; init; }
    public MediaCondition? Media { get; init; }

    /// <summary>The selector compiled ONCE by AngleSharp's selector engine (rules are parsed once and
    /// cached across rebuilds). Null = the engine couldn't parse it — the rule never matches.</summary>
    public AngleSharp.Css.Dom.ISelector? Compiled;

    /// <summary>Bucket key for match candidacy: the rightmost compound's class (preferred), id, or tag.
    /// An element is only tested against rules bucketed under its own tag/classes/id (plus the keyless
    /// bucket), so rules for components not on the page cost nothing. All null = test on every element.</summary>
    public string? KeyClass, KeyId, KeyTag;
}

/// <summary>
/// Minimal CSS parser. Splits a stylesheet into <see cref="CssRule"/>s; selector
/// *matching* is delegated to AngleSharp's selector engine at resolve time. Handles
/// comments, comma-grouped selectors, and <c>prop: value;</c> declarations.
/// </summary>
public static partial class CssParser
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    public static List<CssRule> Parse(string? css)
    {
        var rules = new List<CssRule>();
        if (string.IsNullOrWhiteSpace(css)) return rules;
        ParseInto(CommentRegex().Replace(css, string.Empty), rules, media: null);
        return rules;
    }

    private static void ParseInto(string css, List<CssRule> rules, MediaCondition? media)
    {
        var i = 0;
        while (i < css.Length)
        {
            var open = css.IndexOf('{', i);
            if (open < 0) break;
            var close = MatchBrace(css, open);
            if (close < 0) break;

            var header = css[i..open].Trim();
            var body = css[(open + 1)..close];
            i = close + 1;

            if (header.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            {
                ParseInto(body, rules, ParseMedia(header)); // nested rules inherit the condition
                continue;
            }
            if (header.Length == 0 || header.StartsWith('@'))
                continue; // @keyframes handled elsewhere; skip other at-rules

            var decls = ParseDeclarations(body);
            if (decls.Count == 0) continue;

            foreach (var selRaw in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // Interaction pseudo-classes are matched via marker attributes toggled at runtime.
                var sel = selRaw.Replace(":hover", "[data-hover]").Replace(":active", "[data-active]")
                                .Replace(":focus", "[data-focus]");
                var rule = new CssRule
                {
                    Selector = sel,
                    Specificity = Specificity(sel),
                    Order = rules.Count,
                    Declarations = decls,
                    Media = media,
                    Compiled = SelectorParser.ParseSelector(sel),
                };
                (rule.KeyClass, rule.KeyId, rule.KeyTag) = RightmostKey(sel);
                rules.Add(rule);
            }
        }
    }

    private static int MatchBrace(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static MediaCondition ParseMedia(string header)
    {
        float? Feature(string name)
        {
            var m = Regex.Match(header, name + @"\s*:\s*([\d.]+)px", RegexOptions.IgnoreCase);
            return m.Success && CssNumber.TryParse(m.Groups[1].Value, out var value) ? value : null;
        }
        return new MediaCondition(Feature("min-width"), Feature("max-width"),
                                  Feature("min-height"), Feature("max-height"));
    }

    private static readonly AngleSharp.Css.Parser.CssSelectorParser SelectorParser = new();

    // The bucket key for a (comma-free) selector: the RIGHTMOST compound's class (preferred — most
    // selective), else its id, else its tag. Tokens inside parentheses (`:not(.x)`) or attribute
    // brackets don't identify the subject and are ignored, as are pseudo names. A compound with none
    // of the three (attribute-only / universal) returns all-null → the rule is tested on every element.
    private static (string? Class, string? Id, string? Tag) RightmostKey(string sel)
    {
        string? cls = null, id = null, tag = null;
        int paren = 0, bracket = 0;
        var i = 0;
        var tagPos = true; // a bare ident here would be a type selector (start / after a combinator)
        while (i < sel.Length)
        {
            var c = sel[i];
            if (c == '(') { paren++; i++; continue; }
            if (c == ')') { paren = Math.Max(0, paren - 1); i++; continue; }
            if (paren > 0) { i++; continue; }
            if (c == '[') { bracket++; i++; continue; }
            if (c == ']') { bracket = Math.Max(0, bracket - 1); tagPos = false; i++; continue; }
            if (bracket > 0) { i++; continue; }

            if (char.IsWhiteSpace(c) || c is '>' or '+' or '~')
            {
                cls = id = tag = null; // a combinator: what follows is a NEW rightmost compound
                tagPos = true;
                i++;
                continue;
            }
            if (c is '.' or '#')
            {
                var start = ++i;
                while (i < sel.Length && (char.IsLetterOrDigit(sel[i]) || sel[i] is '-' or '_')) i++;
                if (i > start) { if (c == '.') cls = sel[start..i]; else id = sel[start..i]; }
                tagPos = false;
                continue;
            }
            if (c == ':') // pseudo-class/element: skip the name; a following `(` is handled above
            {
                while (i < sel.Length && sel[i] == ':') i++;
                while (i < sel.Length && (char.IsLetterOrDigit(sel[i]) || sel[i] is '-' or '_')) i++;
                tagPos = false;
                continue;
            }
            if (char.IsLetter(c))
            {
                var start = i;
                while (i < sel.Length && (char.IsLetterOrDigit(sel[i]) || sel[i] is '-' or '_')) i++;
                if (tagPos) tag = sel[start..i].ToLowerInvariant();
                tagPos = false;
                continue;
            }
            tagPos = false; // '*' or anything unrecognised
            i++;
        }
        // Prefer the most selective key; only ONE is used, so a rule lands in exactly one bucket.
        return cls is not null ? (cls, null, null) : id is not null ? (null, id, null) : (null, null, tag);
    }

    public static Dictionary<string, string> ParseDeclarations(string body)
    {
        var decls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var prop = part[..colon].Trim();
            var val = part[(colon + 1)..].Trim();
            if (prop.Length > 0 && val.Length > 0)
                decls[prop] = val;
        }
        return decls;
    }

    /// <summary>Rough CSS specificity: (#id, .class/[attr]/:pseudo, type) packed into one int.</summary>
    private static int Specificity(string selector)
    {
        int ids = 0, classes = 0, types = 0;
        foreach (var token in selector.Split(new[] { ' ', '>', '+', '~' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token;
            for (var k = 0; k < t.Length; k++)
            {
                switch (t[k])
                {
                    case '#': ids++; break;
                    case '.' or '[' or ':': classes++; break;
                }
            }
            // a leading letter (not . # [ : * ) means a type selector
            if (char.IsLetter(t[0])) types++;
        }
        return ids * 10000 + classes * 100 + types;
    }
}
