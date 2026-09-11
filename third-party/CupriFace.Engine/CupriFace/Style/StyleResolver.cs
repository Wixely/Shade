using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using CupriFace.Dom;
using SkiaSharp;

namespace CupriFace.Style;

/// <summary>
/// Builds the render tree from a DOM and resolves computed styles: user-agent
/// defaults → author rules (cascade by specificity then order) → inline style,
/// with inherited properties flowing down from the parent.
/// </summary>
public sealed class StyleResolver
{
    private static readonly Regex _var = new(@"var\(\s*(--[\w-]+)\s*(?:,\s*([^)]*))?\)", RegexOptions.Compiled);

    /// <summary>Resolve var(--token[, fallback]) against the cascaded custom properties.</summary>
    private static string ResolveVars(string value, Dictionary<string, string> props)
    {
        if (!value.Contains("var(", StringComparison.Ordinal)) return value;
        for (var pass = 0; pass < 4 && value.Contains("var(", StringComparison.Ordinal); pass++)
            value = _var.Replace(value, m =>
                props.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Groups[2].Value.Trim());
        return value;
    }

    private readonly SelectorIndex _index;
    private readonly float _viewportWidth;
    private readonly float _viewportHeight;

    /// <summary>
    /// Rules bucketed by their rightmost-compound key. An element is only TESTED against the rules
    /// bucketed under its own tag / classes / id (plus the keyless bucket) — so the whole component
    /// library's CSS costs nothing on elements it can't apply to, and elements inside display:none
    /// subtrees (which BuildChildren prunes) are never matched at all. Matching uses each rule's
    /// selector compiled ONCE at parse time (CssRule.Compiled) — no per-rebuild selector parsing and
    /// no document-wide QuerySelectorAll per rule (which ran on every rebuild AND hover restyle).
    ///
    /// Built once per stylesheet and cached (see <see cref="For"/>): a resolver is constructed on every
    /// rebuild AND every hover restyle, and re-bucketing there made those costs scale with the SIZE OF
    /// THE STYLESHEET rather than the page — +800 unused rules doubled a rebuild.
    /// </summary>
    private sealed class SelectorIndex
    {
        public readonly Dictionary<string, List<CssRule>> ByClass = new();
        public readonly Dictionary<string, List<CssRule>> ById = new();
        public readonly Dictionary<string, List<CssRule>> ByTag = new();
        public readonly List<CssRule> Keyless = new();

        public SelectorIndex(List<CssRule> rules)
        {
            foreach (var rule in rules)
            {
                if (rule.Compiled is null) continue; // selector the engine couldn't parse — never matches
                if (rule.KeyClass is { } c) Bucket(ByClass, c, rule);
                else if (rule.KeyId is { } i) Bucket(ById, i, rule);
                else if (rule.KeyTag is { } t) Bucket(ByTag, t, rule);
                else Keyless.Add(rule);
            }
        }

        private static void Bucket(Dictionary<string, List<CssRule>> map, string key, CssRule rule)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<CssRule>();
            list.Add(rule);
        }
    }

    // Keyed by the rule-list instance, which the document parses once and reuses for the life of the
    // stylesheet; the weak table lets a discarded document's index be collected with it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<CssRule>, SelectorIndex> _indexes = new();

    private static SelectorIndex For(List<CssRule> rules) => _indexes.GetValue(rules, static r => new SelectorIndex(r));

    private static readonly Comparison<CssRule> Cascade =
        static (a, b) => a.Specificity != b.Specificity ? a.Specificity - b.Specificity : a.Order - b.Order;

    public StyleResolver(List<CssRule> rules, float viewportWidth = 1024f, float viewportHeight = 768f)
    {
        _index = For(rules);
        _viewportWidth = viewportWidth;
        _viewportHeight = viewportHeight;
    }

    public RenderNode BuildTree(IDocument document)
    {
        var body = document.Body ?? throw new InvalidOperationException("Document has no <body>.");
        var root = new RenderNode { Tag = "body", Element = body };

        // The render tree starts at <body>, so <html> is never a node — but `:root { --token: … }`
        // is THE conventional place for a stylesheet's palette, and CSS says custom properties (and
        // the inherited text properties) flow from the document element down. Resolve a style for
        // the document element off to the side and hand it to body as its inheritance parent: the
        // rules land in the normal buckets (`html` by tag, `:root` keyless — AngleSharp matches it
        // against the document element natively), so cascade, @media and var() all behave. The
        // document element is an inheritance ENVIRONMENT here, not a box: its layout and paint
        // properties (width, background, …) are resolved but never consumed — declare those on body.
        ComputedStyle? env = null;
        if (document.DocumentElement is { } html)
        {
            var envNode = new RenderNode { Tag = "html", Element = html };
            ResolveStyle(envNode, parent: null);
            env = envNode.Style;
        }
        ResolveStyle(root, env);
        BuildChildren(root, body);
        return root;
    }

    /// <summary>The rules matching this element, in cascade order (or null when none match).</summary>
    private List<CssRule>? MatchRules(IElement el)
    {
        List<CssRule>? matched = null;
        if (_index.ByTag.TryGetValue(el.LocalName, out var bt)) Test(el, bt, ref matched);
        foreach (var cls in el.ClassList)
            if (_index.ByClass.TryGetValue(cls, out var bc)) Test(el, bc, ref matched);
        if (el.Id is { Length: > 0 } id && _index.ById.TryGetValue(id, out var bi)) Test(el, bi, ref matched);
        if (_index.Keyless.Count > 0) Test(el, _index.Keyless, ref matched);
        if (matched is { Count: > 1 }) matched.Sort(Cascade); // candidates per element are few
        return matched;
    }

    private void Test(IElement el, List<CssRule> bucket, ref List<CssRule>? matched)
    {
        foreach (var rule in bucket)
        {
            if (rule.Media is { } m && !m.Matches(_viewportWidth, _viewportHeight)) continue; // @media gate
            if (rule.Compiled!.Match(el, null)) (matched ??= new List<CssRule>()).Add(rule);
        }
    }

    private void BuildChildren(RenderNode parentNode, IElement parentEl)
    {
        // Track collapsed whitespace between siblings so the inline formatting context keeps the space
        // between flowed runs (e.g. the space in "text <code>x</code>" or between <b>a</b> <b>b</b>).
        // Whitespace-only text isn't added as a node (so flex/grid children are unchanged); instead it
        // flags the previous node's WsAfter and the next node's WsBefore.
        RenderNode? prev = null;
        var pendingWs = false;
        foreach (var child in parentEl.ChildNodes)
        {
            switch (child)
            {
                case IElement el:
                    var tag = el.LocalName.ToLowerInvariant();
                    if (tag is "script" or "style" or "head" or "meta" or "link" or "title") continue;
                    var node = new RenderNode { Tag = tag, Element = el, WsBefore = pendingWs };
                    parentNode.AddChild(node);
                    ResolveStyle(node, parentNode.Style);
                    node.IconPath = el.GetAttribute("data-cupri-icon"); // set by icon-bearing components
                    node.ImageSrc = el.GetAttribute("data-cupri-image"); // set by <cupri-image> (and video posters)
                    node.SurfaceKey = el.GetAttribute("data-cupri-surface"); // set by <cupri-video> (live frames)
                    node.ChartLine = el.GetAttribute("data-cupri-line"); // set by <cupri-line-chart>/<cupri-sparkline>
                    if (node.Style.Display != DisplayType.None)
                        BuildChildren(node, el);
                    prev = node; pendingWs = false;
                    break;

                case IText t:
                    var text = t.Text;
                    // Whitespace-only text is a SEPARATOR, not a node — except where the parent
                    // preserves whitespace (pre/pre-wrap: a blank line IS content), and except when
                    // it contains a no-break space, which CSS does not count as collapsible at all:
                    // an element whose only content is &nbsp; must keep its height (#69).
                    var ws = parentNode.Style.WhiteSpace;
                    var preserveAll = ws is WhiteSpaceMode.Pre or WhiteSpaceMode.PreWrap;
                    if (text.Length == 0 || (!preserveAll && IsAllCollapsible(text)))
                    {
                        if (prev is not null) prev.WsAfter = true;
                        pendingWs = true;
                        continue;
                    }
                    var textNode = new RenderNode
                    {
                        Tag = "#text", Text = NormalizeText(text, ws),
                        WsBefore = pendingWs || IsCollapsible(text[0]),
                        WsAfter = IsCollapsible(text[^1]),
                    };
                    parentNode.AddChild(textNode);
                    // Text inherits the parent's computed style directly.
                    textNode.Style.InheritFrom(parentNode.Style);
                    textNode.Style.Display = DisplayType.Inline;
                    prev = textNode; pendingWs = false;
                    break;
            }
        }
    }

    // The COLLAPSIBLE whitespace set — deliberately not char.IsWhiteSpace, which also matches the
    // no-break space (U+00A0): CSS collapses DOCUMENT whitespace, but &nbsp; exists precisely to
    // occupy space, so it rides through normalisation like any other glyph (#69).
    private static bool IsCollapsible(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static bool IsAllCollapsible(string s)
    {
        foreach (var c in s) if (!IsCollapsible(c)) return false;
        return true;
    }

    /// <summary>Per-mode text normalisation (#69). Normal/nowrap collapse every collapsible run to
    /// one space; pre-line keeps newlines — a run containing one becomes exactly one <c>'\n'</c> —
    /// and collapses the rest; pre/pre-wrap keep everything verbatim, with line endings normalised
    /// to <c>'\n'</c>. Leading/trailing runs are trimmed in the collapsing modes (including the
    /// pre-line newline: the run around a bound <c>{{value}}</c> is usually markup indentation, and
    /// a blank first line from source formatting is never what the author meant); the preserving
    /// modes keep their edges, because a code block's leading newline is the author's to spend.</summary>
    private static string NormalizeText(string s, WhiteSpaceMode mode)
    {
        if (mode is WhiteSpaceMode.Pre or WhiteSpaceMode.PreWrap)
            return s.Contains('\r') ? s.Replace("\r\n", "\n").Replace('\r', '\n') : s;

        var sb = new System.Text.StringBuilder(s.Length);
        bool run = false, runHadNewline = false;
        foreach (var ch in s)
        {
            if (IsCollapsible(ch))
            {
                run = true;
                if (ch is '\n' or '\r') runHadNewline = true;
                continue;
            }
            if (run)
            {
                if (sb.Length > 0) sb.Append(mode == WhiteSpaceMode.PreLine && runHadNewline ? '\n' : ' ');
                run = false; runHadNewline = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private void ResolveStyle(RenderNode node, ComputedStyle? parent)
    {
        var style = node.Style;
        if (parent is not null) style.InheritFrom(parent);

        ApplyUserAgentDefaults(node);

        // Author rules in cascade order (matched on the fly, bucketed); inline style wins last.
        var rules = node.Element is { } el ? MatchRules(el) : null;
        Dictionary<string, string>? inlineDecls = null;
        var inline = node.Element?.GetAttribute("style");
        if (!string.IsNullOrWhiteSpace(inline)) inlineDecls = CssParser.ParseDeclarations(inline);

        // Pass 1: custom properties (--tokens) cascade + inherit into CustomProps (copy-on-write:
        // the parent's dictionary is shared until this node actually declares one).
        if (rules is not null) foreach (var rule in rules) CollectCustomProps(style, rule.Declarations);
        if (inlineDecls is not null) CollectCustomProps(style, inlineDecls);

        // Pass 2: normal properties, with var() resolved against the final tokens.
        if (rules is not null)
            foreach (var rule in rules)
                SawViewportUnit |= Apply(style, rule.Declarations, _viewportWidth, _viewportHeight);
        if (inlineDecls is not null)
            SawViewportUnit |= Apply(style, inlineDecls, _viewportWidth, _viewportHeight);
    }

    private static void CollectCustomProps(ComputedStyle style, Dictionary<string, string> decls)
    {
        foreach (var (k, v) in decls)
            if (k.StartsWith("--", StringComparison.Ordinal))
                style.OwnCustomProps()[k] = ResolveVars(v, style.CustomProps);
    }

    private static void ApplyUserAgentDefaults(RenderNode node)
    {
        var s = node.Style;
        switch (node.Tag)
        {
            case "div" or "p" or "section" or "header" or "footer" or "main" or "article" or "nav" or "ul" or "ol" or "li":
                s.Display = DisplayType.Block; break;
            case "span" or "a" or "strong" or "b" or "em" or "i" or "small" or "label"
                or "code" or "kbd" or "samp" or "mark" or "abbr" or "cite" or "q"
                or "sub" or "sup" or "time" or "u" or "s" or "del" or "ins" or "var":
                s.Display = DisplayType.Inline; break;
            case "h1": s.Display = DisplayType.Block; s.FontSize = 32; s.FontWeight = 700; break;
            case "h2": s.Display = DisplayType.Block; s.FontSize = 24; s.FontWeight = 700; break;
            case "h3": s.Display = DisplayType.Block; s.FontSize = 19; s.FontWeight = 700; break;
        }
        if (node.Tag is "strong" or "b") s.FontWeight = 700;
        if (node.Tag is "code" or "kbd" or "samp" or "var") s.FontFamily = "monospace";
        if (node.Tag is "em" or "i" or "cite" or "address" or "dfn" or "var") s.FontStyle = FontSlant.Italic;
        if (node.Tag is "u" or "ins") s.Decorations |= TextDecorations.Underline;
        if (node.Tag is "s" or "del" or "strike") s.Decorations |= TextDecorations.LineThrough;
        // A link defaults to the copper accent colour AND an underline, as browsers do — colour alone
        // is not a sufficient cue (WCAG 1.4.1). Both are plain CSS defaults: `a { text-decoration:none }`.
        if (node.Tag == "a" && node.Element?.HasAttribute("href") == true)
        {
            s.Color = new SKColor(0xB8, 0x73, 0x33);
            s.Decorations |= TextDecorations.Underline;
        }
    }

    /// <summary>Apply a declaration block onto a style (used by the animation system). The viewport
    /// is optional because a keyframe block is parsed without one; viewport units left unresolved
    /// become <c>auto</c> rather than a definite zero (see <see cref="SubstituteViewportUnits"/>).</summary>
    public static void ApplyDeclarations(ComputedStyle s, Dictionary<string, string> decls,
        float viewportWidth = 0f, float viewportHeight = 0f) => Apply(s, decls, viewportWidth, viewportHeight);

    /// <summary>True once any declaration this resolver applied used a viewport-relative length.
    /// The document watches this: such a document must re-resolve when the viewport changes, the
    /// same way an <c>@media</c> one does, or its lengths stay pinned to the size they were first
    /// resolved at.</summary>
    public bool SawViewportUnit { get; private set; }

    /// <summary>Returns whether any declaration in the block used a viewport-relative length.</summary>
    private static bool Apply(ComputedStyle s, Dictionary<string, string> decls, float vw, float vh)
    {
        var sawViewportUnit = false;
        foreach (var (propRaw, valueRaw) in decls)
        {
            var prop = propRaw.ToLowerInvariant();
            if (prop.StartsWith("--", StringComparison.Ordinal)) continue; // custom props: pass 1
            var v = SubstituteViewportUnits(ResolveVars(valueRaw.Trim(), s.CustomProps), vw, vh, out var usedVp);
            sawViewportUnit |= usedVp;
            switch (prop)
            {
                case "display": s.Display = ParseDisplay(v); break;
                case "position": s.Position = v.ToLowerInvariant() switch { "relative" => PositionType.Relative, "absolute" => PositionType.Absolute, "fixed" => PositionType.Fixed, "sticky" => PositionType.Sticky, _ => PositionType.Static }; break;
                case "z-index": s.ZIndex = (int)ParseNum(v); break;
                case "overflow": s.Overflow = v.ToLowerInvariant() switch { "hidden" => OverflowMode.Hidden, "scroll" or "auto" => OverflowMode.Scroll, _ => OverflowMode.Visible }; break;
                // Anything that is not border-box is content-box, which is also the CSS initial
                // value — so an unreadable value falls back to the standard, not to a surprise.
                case "box-sizing": s.BorderBox = v.Trim().ToLowerInvariant() == "border-box"; break;
                case "resize": s.Resize = v.ToLowerInvariant() switch { "both" => ResizeMode.Both, "horizontal" => ResizeMode.Horizontal, "vertical" => ResizeMode.Vertical, _ => ResizeMode.None }; break;

                case "width": s.Width = ParseLen(v); break;
                case "height": s.Height = ParseLen(v); break;
                case "min-width": s.MinWidth = ParseLen(v); break;
                case "min-height": s.MinHeight = ParseLen(v); break;
                case "max-width": s.MaxWidth = ParseLen(v); break;
                case "max-height": s.MaxHeight = ParseLen(v); break;

                case "margin": s.Margin = ParseEdges(v); break;
                case "margin-top": s.Margin.Top = ParseLen(v); break;
                case "margin-right": s.Margin.Right = ParseLen(v); break;
                case "margin-bottom": s.Margin.Bottom = ParseLen(v); break;
                case "margin-left": s.Margin.Left = ParseLen(v); break;

                case "padding": s.Padding = ParseEdges(v); break;
                case "padding-top": s.Padding.Top = ParseLen(v); break;
                case "padding-right": s.Padding.Right = ParseLen(v); break;
                case "padding-bottom": s.Padding.Bottom = ParseLen(v); break;
                case "padding-left": s.Padding.Left = ParseLen(v); break;

                case "top": s.Top = ParseLen(v); break;
                case "right": s.Right = ParseLen(v); break;
                case "bottom": s.Bottom = ParseLen(v); break;
                case "left": s.Left = ParseLen(v); break;

                case "flex-direction": s.FlexDirection = ParseFlexDir(v); break;
                case "flex-wrap": s.FlexWrap = v.Contains("wrap", StringComparison.OrdinalIgnoreCase) && !v.Contains("nowrap", StringComparison.OrdinalIgnoreCase) ? FlexWrapMode.Wrap : FlexWrapMode.NoWrap; break;
                case "justify-content": s.JustifyContent = ParseJustify(v); break;
                case "align-items": s.AlignItems = ParseAlign(v); break;
                case "justify-items": s.JustifyItems = ParseAlign(v); break;
                case "gap": { var g = ParsePx(v); s.RowGap = s.ColumnGap = g; break; }
                case "row-gap": s.RowGap = ParsePx(v); break;
                case "column-gap": s.ColumnGap = ParsePx(v); break;

                case "flex-grow": s.FlexGrow = ParseNum(v); break;
                case "flex-shrink": s.FlexShrink = ParseNum(v); break;
                case "flex-basis": s.FlexBasis = ParseLen(v); break;
                case "flex": ParseFlexShorthand(s, v); break;

                case "grid-template-columns": s.GridTemplateColumns = ParseTemplate(v, out s.GridColumnLines, out s.GridRepeatColumns); break;
                case "grid-template-rows": s.GridTemplateRows = ParseTemplate(v, out s.GridRowLines, out s.GridRepeatRows); break;
                case "grid-auto-rows": s.GridAutoRows = ParseTrack(v); break;
                case "grid-column": s.GridColumn = ParsePlacement(v); break;
                case "grid-row": s.GridRow = ParsePlacement(v); break;

                case "border": ParseBorderShorthand(s, v); break;
                case "border-width": { var w = ParsePx(v); s.BorderTop = s.BorderRight = s.BorderBottom = s.BorderLeft = w; break; }
                case "border-color": if (Colors.TryParse(v, out var bc)) s.BorderColor = bc; break;
                case "border-style": if (ParseBorderStyle(v) is { } st) s.BorderStyle = st; break;
                case "border-radius": s.BorderRadius = ParsePx(v); break;

                case "background":
                    if (ParseGradient(v) is { } bgGrad) { s.BackgroundGradient = bgGrad; s.Background = SKColors.Transparent; }
                    else if (Colors.TryParse(v, out var bg)) { s.Background = bg; s.BackgroundGradient = null; }
                    break;
                case "background-image": s.BackgroundGradient = ParseGradient(v); break; // gradient over bg-color; 'none' clears
                case "background-color": if (Colors.TryParse(v, out var bgc)) s.Background = bgc; break;
                case "opacity": s.Opacity = Math.Clamp(ParseNum(v), 0f, 1f); break;
                case "transform": ParseTransform(s, v); break;
                case "transform-origin": ParseTransformOrigin(s, v); break;
                case "animation": ParseAnimation(s, v); break;
                case "animation-name": s.AnimationName = v; break;
                case "animation-duration": s.AnimationDuration = ParseSeconds(v); break;
                case "animation-delay": s.AnimationDelay = ParseSeconds(v); break;
                case "animation-iteration-count": s.AnimationIterations = ParseIterations(v); break;
                case "animation-fill-mode": ParseFillMode(s, v); break;
                case "transition": ParseTransition(s, v); break;
                case "filter": ParseFilter(s, v); break;
                case "backdrop-filter" or "-webkit-backdrop-filter": s.BackdropFilter = ParseFilterOps(v); break;
                case "box-shadow": ParseBoxShadow(s, v); break;

                case "color": if (Colors.TryParse(v, out var col)) s.Color = col; break;
                case "font-size": s.FontSize = ParsePx(v, s.FontSize); break;
                case "font-weight": s.FontWeight = ParseWeight(v); break;
                case "font-family": s.FontFamily = v.Split(',')[0].Trim().Trim('"', '\''); break;
                case "line-height": s.LineHeight = ParseLineHeight(v); break;
                case "text-align": s.TextAlign = v.ToLowerInvariant() switch { "center" => TextAlign.Center, "right" => TextAlign.Right, _ => TextAlign.Left }; break;
                case "white-space":
                    s.WhiteSpace = v.Trim().ToLowerInvariant() switch
                    {
                        "nowrap" => WhiteSpaceMode.NoWrap,
                        "pre" => WhiteSpaceMode.Pre,
                        "pre-wrap" => WhiteSpaceMode.PreWrap,
                        "pre-line" => WhiteSpaceMode.PreLine,
                        _ => WhiteSpaceMode.Normal,
                    };
                    break;
                case "word-break": s.WordBreakAll = v.Trim().ToLowerInvariant() == "break-all"; break;
                case "overflow-wrap" or "word-wrap":
                    s.OverflowWrapBreak = v.Trim().ToLowerInvariant() is "break-word" or "anywhere"; break;
                case "cursor": s.Cursor = ParseCursor(v); break;
                case "font-style":
                    s.FontStyle = v.Trim().ToLowerInvariant() switch
                    {
                        "italic" => FontSlant.Italic,
                        "oblique" => FontSlant.Oblique,
                        _ => FontSlant.Normal,
                    };
                    break;
                // Shorthand and longhand both land here: we only support the *line* part, so any
                // colour/style words in the shorthand are ignored rather than mis-parsed.
                case "text-decoration" or "text-decoration-line": s.Decorations = ParseDecorations(v); break;
            }
        }
        return sawViewportUnit;
    }

    // "underline", "line-through overline", "none", … → flags. Unknown words are ignored, so the
    // shorthand's colour/style parts (`underline wavy red`) still yield the right line.
    private static TextDecorations ParseDecorations(string v)
    {
        var d = TextDecorations.None;
        foreach (var word in v.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            d |= word switch
            {
                "underline" => TextDecorations.Underline,
                "line-through" => TextDecorations.LineThrough,
                "overline" => TextDecorations.Overline,
                _ => TextDecorations.None, // includes "none", which leaves it cleared
            };
        return d;
    }

    // CSS cursor keyword → the supported subset (synonyms fold together: the diagonal/axis resize
    // keywords collapse onto the two double-arrows a typical platform cursor set actually provides).
    private static CursorType ParseCursor(string v) => v.Trim().ToLowerInvariant() switch
    {
        "default" => CursorType.Default,
        "pointer" => CursorType.Pointer,
        "text" or "vertical-text" => CursorType.Text,
        "wait" => CursorType.Wait,
        "progress" => CursorType.Progress,
        "help" => CursorType.Help,
        "crosshair" or "cell" => CursorType.Crosshair,
        "move" or "all-scroll" => CursorType.Move,
        "not-allowed" or "no-drop" => CursorType.NotAllowed,
        "grab" => CursorType.Grab,
        "grabbing" => CursorType.Grabbing,
        "col-resize" or "e-resize" or "w-resize" or "ew-resize" => CursorType.EwResize,
        "row-resize" or "n-resize" or "s-resize" or "ns-resize" => CursorType.NsResize,
        "nwse-resize" or "nw-resize" or "se-resize" => CursorType.NwseResize,
        "nesw-resize" or "ne-resize" or "sw-resize" => CursorType.NeswResize,
        "none" => CursorType.None,
        _ => CursorType.Auto, // auto / unknown → let the document infer
    };

    // ---- value parsers -------------------------------------------------------
    private static DisplayType ParseDisplay(string v) => v.ToLowerInvariant() switch
    {
        "flex" or "inline-flex" => DisplayType.Flex,
        "grid" or "inline-grid" => DisplayType.Grid,
        "inline-block" => DisplayType.InlineBlock,
        "inline" => DisplayType.Inline,
        "none" => DisplayType.None,
        _ => DisplayType.Block,
    };

    private static FlexDirection ParseFlexDir(string v) => v.ToLowerInvariant() switch
    {
        "row-reverse" => FlexDirection.RowReverse,
        "column" => FlexDirection.Column,
        "column-reverse" => FlexDirection.ColumnReverse,
        _ => FlexDirection.Row,
    };

    private static JustifyContent ParseJustify(string v) => v.ToLowerInvariant() switch
    {
        "center" => JustifyContent.Center,
        "flex-end" or "end" => JustifyContent.FlexEnd,
        "space-between" => JustifyContent.SpaceBetween,
        "space-around" => JustifyContent.SpaceAround,
        "space-evenly" => JustifyContent.SpaceEvenly,
        _ => JustifyContent.FlexStart,
    };

    private static AlignItems ParseAlign(string v) => v.ToLowerInvariant() switch
    {
        "center" => AlignItems.Center,
        "flex-start" or "start" => AlignItems.FlexStart,
        "flex-end" or "end" => AlignItems.FlexEnd,
        _ => AlignItems.Stretch,
    };

    /// <summary>Rewrite viewport-relative lengths to px BEFORE any property parser sees them.
    ///
    /// Done here, once, for two reasons. Every length-bearing property gets them for free —
    /// width/height, insets, padding, margins, gaps, grid tracks, shadows, transforms — and so do
    /// <c>calc()</c> terms, because calc is parsed downstream of this. And unlike <c>%</c>, a
    /// viewport unit is absolute the moment the viewport is known: it does not depend on the
    /// containing block, so it needs no layout-time basis and can be folded in at style time. The
    /// resolver is rebuilt with the live viewport on every frame, so a resize re-resolves.
    ///
    /// <c>dvh</c>/<c>svh</c>/<c>lvh</c> (and the vw forms) are treated as plain <c>vh</c>/<c>vw</c>:
    /// a CupriFace surface has no browser chrome that grows or shrinks, so the dynamic, small and
    /// large viewports are all the same box.
    ///
    /// With no viewport (a keyframe block), the token is left alone and <see cref="ParseLen"/>
    /// turns it into <c>auto</c> — never a definite zero. That mattered: <c>height:100vh</c> was
    /// parsed as a definite <c>0px</c>, which under <c>overflow:hidden</c> clipped an entire
    /// populated subtree away and rendered a black screen (#71).</summary>
    private static string SubstituteViewportUnits(string value, float vw, float vh, out bool used)
    {
        used = false;
        // Fast path: the overwhelming majority of declarations have no viewport unit, and this
        // runs for every declaration of every element on every rebuild.
        if (value.IndexOf('v') < 0 && value.IndexOf('V') < 0) return value;

        // No viewport (a keyframe block): report the usage but leave the token, so ParseLen makes
        // it auto rather than a definite zero.
        if (vw <= 0f || vh <= 0f)
        {
            used = ViewportUnitPattern.IsMatch(value);
            return value;
        }

        var found = false;
        var result = ViewportUnitPattern.Replace(value, m =>
        {
            found = true;
            if (!CssNumber.TryParse(m.Groups[1].Value, out var n)) return m.Value;
            var basis = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "vmin" => MathF.Min(vw, vh),
                "vmax" => MathF.Max(vw, vh),
                var u when u.EndsWith('w') => vw,
                _ => vh,
            };
            return (n / 100f * basis).ToString("0.####", CultureInfo.InvariantCulture) + "px";
        });
        used = found;
        return result;
    }

    // Longer units first so vmin/vmax and the d/s/l forms win over a bare vh/vw. The trailing
    // boundary keeps `12vhx` (and identifiers) from matching.
    private static readonly Regex ViewportUnitPattern = new(
        @"(-?(?:\d+\.?\d*|\.\d+))(vmin|vmax|dvh|svh|lvh|dvw|svw|lvw|vh|vw)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Length ParseLen(string v)
    {
        v = v.Trim();
        if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Length.Auto;
        // The intrinsic-size keywords must not fall through to the px parser: ParsePx's fallback
        // is 0, so `width:max-content` silently became a DEFINITE 0px — a 20px padding-only box
        // with every text line spilling out of it, which is exactly how it looked on a phone.
        // Auto is the honest mapping here: for the boxes these keywords are used on (fixed-position
        // popups, shrink-wrapped chips) the engine's auto width already resolves to max-content,
        // clamped by max-width.
        if (v.Equals("max-content", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("min-content", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("fit-content", StringComparison.OrdinalIgnoreCase)) return Length.Auto;
        if (v.StartsWith("calc(", StringComparison.OrdinalIgnoreCase)) return ParseCalc(v);
        if (v.EndsWith('%') && CssNumber.TryParse(v[..^1], out var pct)) return new Length(LengthUnit.Percent, pct);
        // A unit we cannot read must not fall through to a DEFINITE 0px. That is the same trap the
        // intrinsic keywords above are guarded against, and it is what made an unsupported
        // `height:100vh` collapse a full-screen container to nothing: with `overflow:hidden` the
        // zero-height clip hid an entire populated subtree, so the app painted a complete display
        // list into a black screen (#71). Auto is the honest answer for a length we do not know.
        return TryParsePx(v, out var px) ? new Length(LengthUnit.Px, px) : Length.Auto;
    }

    // Simple calc(): sum of signed px and % terms, e.g. calc(100% - 40px), calc(50% + 8px).
    private static Length ParseCalc(string v)
    {
        var inner = v[(v.IndexOf('(') + 1)..v.LastIndexOf(')')];
        inner = inner.Replace("+", " + ").Replace("-", " - ");
        var tokens = inner.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        float px = 0, percent = 0, sign = 1;
        foreach (var tok in tokens)
        {
            if (tok == "+") { sign = 1; continue; }
            if (tok == "-") { sign = -1; continue; }
            if (tok.EndsWith('%') && CssNumber.TryParse(tok[..^1], out var p)) percent += sign * p;
            else px += sign * ParsePx(tok);
            sign = 1;
        }
        return Length.Calc(px, percent);
    }

    private static LengthEdges ParseEdges(string v)
    {
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Length t, r, b, l;
        switch (parts.Length)
        {
            case 1: t = r = b = l = ParseLen(parts[0]); break;
            case 2: t = b = ParseLen(parts[0]); r = l = ParseLen(parts[1]); break;
            case 3: t = ParseLen(parts[0]); r = l = ParseLen(parts[1]); b = ParseLen(parts[2]); break;
            default: t = ParseLen(parts[0]); r = ParseLen(parts[1]); b = ParseLen(parts[2]); l = ParseLen(parts[3]); break;
        }
        return new LengthEdges { Top = t, Right = r, Bottom = b, Left = l };
    }

    private static float ParsePx(string v, float fallback = 0f) => TryParsePx(v, out var px) ? px : fallback;

    /// <summary>The px parse, but able to say "that is not a length I understand" — which
    /// <see cref="ParseLen"/> needs in order to answer <c>auto</c> instead of a definite zero.</summary>
    private static bool TryParsePx(string v, out float px)
    {
        v = v.Trim().ToLowerInvariant();
        if (v.EndsWith("px")) v = v[..^2];
        else if (v.EndsWith("rem") || v.EndsWith("em")) { if (CssNumber.TryParse(v.TrimEnd('r', 'e', 'm'), out var em)) { px = em * 16f; return true; } }
        return CssNumber.TryParse(v, out px);
    }

    private static float ParseNum(string v) => CssNumber.TryParse(v.Trim(), out var n) ? n : 0f;

    private static int ParseWeight(string v) => v.ToLowerInvariant() switch
    {
        "bold" or "bolder" => 700,
        "normal" or "lighter" => 400,
        _ => int.TryParse(v, out var w) ? w : 400,
    };

    private static float ParseLineHeight(string v)
    {
        v = v.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)) return ParsePx(v) / 16f; // rough; refined once font-size known
        return CssNumber.TryParse(v, out var n) ? n : 1.2f;
    }

    // ---- grid parsers --------------------------------------------------------
    private static List<TrackSize> ParseTracks(string v) => ParseTemplate(v, out _, out _);

    /// <summary>Parse a grid template into tracks plus any <c>[name]</c> line names → 1-based line
    /// index. A numeric <c>repeat(n, …)</c> expands inline; <c>repeat(auto-fill|auto-fit, …)</c>
    /// comes out through <paramref name="autoRepeat"/> because its count is a function of the
    /// container size, which only layout knows. Line names declared AFTER an auto repeat would
    /// mis-number once it materialises — that combination is not supported.</summary>
    private static List<TrackSize> ParseTemplate(string v,
        out System.Collections.Generic.Dictionary<string, int>? lineNames, out GridAutoRepeat? autoRepeat)
    {
        lineNames = null;
        autoRepeat = null;
        var tracks = new List<TrackSize>();
        foreach (var tok in SplitTopLevel(v))
        {
            if (tok.Length > 1 && tok[0] == '[' && tok[^1] == ']') // [a b] names the line before the next track
            {
                var line = tracks.Count + 1; // 1-based line number
                foreach (var name in tok[1..^1].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    (lineNames ??= new())[name] = line;
            }
            else if (tok.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase) && tok.EndsWith(')'))
            {
                // SplitTopLevel kept the whole call as one token, so the pattern may itself contain
                // minmax(a, b) — split the count from the pattern at the first TOP-LEVEL comma (a
                // regex that stopped at the first ')' mangled every nested track function).
                var inner = tok[7..^1];
                var comma = TopLevelComma(inner);
                if (comma < 0) continue;
                var countStr = inner[..comma].Trim().ToLowerInvariant();
                var pattern = new List<TrackSize>();
                foreach (var p in SplitTopLevel(inner[(comma + 1)..])) pattern.Add(ParseTrack(p));
                if (pattern.Count == 0) continue;

                if (int.TryParse(countStr, out var count))
                    for (var r = 0; r < count; r++) tracks.AddRange(pattern);
                else if (countStr is "auto-fill" or "auto-fit")
                    // Deferred to LayoutGrid. At most one per template (as in CSS) — a second is dropped.
                    autoRepeat ??= new GridAutoRepeat(pattern, tracks.Count, countStr == "auto-fit");
            }
            else tracks.Add(ParseTrack(tok));
        }
        return tracks;
    }

    /// <summary>Index of the first comma at paren depth 0, or −1 — <c>repeat()</c>'s count/pattern
    /// separator, which a comma inside a nested <c>minmax(a, b)</c> must not be mistaken for.</summary>
    private static int TopLevelComma(string v)
    {
        var depth = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Split on spaces but keep parenthesised groups (minmax(...)) and [name] lists intact.</summary>
    private static List<string> SplitTopLevel(string v)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var ch in v)
        {
            if (ch is '(' or '[') depth++;
            else if (ch is ')' or ']') depth--;
            if (char.IsWhiteSpace(ch) && depth == 0)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    private static TrackSize ParseTrack(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v == "auto") return TrackSize.Auto;
        if (v.StartsWith("minmax("))
        {
            var inner = v[7..v.LastIndexOf(')')].Split(',', StringSplitOptions.TrimEntries);
            var min = inner.Length > 0 ? ParsePx(inner[0]) : 0f;
            var max = inner.Length > 1 ? ParseTrack(inner[1]) : new TrackSize(TrackKind.Fraction, 1);
            return new TrackSize(max.Kind, max.Value, minPx: min);
        }
        if (v.EndsWith("fr")) return new TrackSize(TrackKind.Fraction, CssNumber.TryParse(v[..^2], out var fr) ? fr : 1);
        if (v.EndsWith('%')) return new TrackSize(TrackKind.Percent, CssNumber.TryParse(v[..^1], out var p) ? p : 0);
        return new TrackSize(TrackKind.Px, ParsePx(v));
    }

    private static GridPlacement ParsePlacement(string v)
    {
        // Supports: "2", "span 3", "2 / 4", "2 / span 3", and named lines ("main / main-end").
        v = v.Trim().ToLowerInvariant();
        var sides = v.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        int? start = null;
        var span = 1;
        string? startName = null, endName = null;
        if (sides.Length >= 1)
        {
            if (sides[0].StartsWith("span")) span = int.TryParse(sides[0][4..].Trim(), out var sp) ? sp : 1;
            else if (int.TryParse(sides[0], out var st)) start = st;
            else startName = sides[0]; // named grid line
        }
        if (sides.Length >= 2)
        {
            var end = sides[1];
            if (end.StartsWith("span")) span = int.TryParse(end[4..].Trim(), out var sp2) ? sp2 : 1;
            else if (int.TryParse(end, out var e) && start is { } s0) span = Math.Max(1, e - s0);
            else if (!int.TryParse(end, out _)) endName = end; // named end line
        }
        return new GridPlacement(start, span, startName, endName);
    }

    private static void ParseAnimation(ComputedStyle s, string v)
    {
        // animation: <name> <duration> [timing] [delay] [iteration-count] [direction] [fill-mode].
        // A shorthand resets every longhand (CSS semantics). The first time token is the duration
        // and the second the delay; a bare number is the iteration count; the name is whatever
        // token is none of those and not a keyword.
        s.AnimationName = null; s.AnimationDuration = 0f; s.AnimationDelay = 0f; s.AnimationIterations = 1f;
        s.AnimationFillForwards = s.AnimationFillBackwards = false;
        var times = 0;
        foreach (var tok in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var low = tok.ToLowerInvariant();
            if (char.IsDigit(low[0]) || (low.Length > 1 && low[0] is '-' or '.' or '+' && (char.IsDigit(low[1]) || low[1] == '.')))
            {
                if (low.EndsWith('s')) { if (times++ == 0) s.AnimationDuration = ParseSeconds(low); else s.AnimationDelay = ParseSeconds(low); }
                else if (CssNumber.TryParse(low, out var n)) s.AnimationIterations = Math.Max(0f, n);
                continue;
            }
            switch (low)
            {
                case "infinite": s.AnimationIterations = float.PositiveInfinity; break;
                case "forwards": s.AnimationFillForwards = true; break;
                case "backwards": s.AnimationFillBackwards = true; break;
                case "both": s.AnimationFillForwards = s.AnimationFillBackwards = true; break;
                case "none" or "linear" or "ease" or "ease-in" or "ease-out" or "ease-in-out" or "step-start" or "step-end"
                     or "normal" or "reverse" or "alternate" or "alternate-reverse" or "running" or "paused": break;
                default:
                    if (low.StartsWith("cubic-bezier") || low.StartsWith("steps")) break;
                    s.AnimationName ??= tok;
                    break;
            }
        }
    }

    private static float ParseIterations(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v == "infinite") return float.PositiveInfinity;
        return CssNumber.TryParse(v, out var n) ? Math.Max(0f, n) : 1f;
    }

    private static void ParseFillMode(ComputedStyle s, string v)
    {
        v = v.Trim().ToLowerInvariant();
        s.AnimationFillForwards = v is "forwards" or "both";
        s.AnimationFillBackwards = v is "backwards" or "both";
    }

    private static float ParseSeconds(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v.EndsWith("ms")) return CssNumber.TryParse(v[..^2], out var ms) ? ms / 1000f : 0f;
        if (v.EndsWith('s')) return CssNumber.TryParse(v[..^1], out var sec) ? sec : 0f;
        return CssNumber.TryParse(v, out var n) ? n : 0f;
    }

    // transition: <prop|all> <duration> [timing] [delay] [, <prop> <duration> …]. A later `transition`
    // declaration replaces the whole list (CSS shorthand semantics). Splitting is paren-aware so a
    // cubic-bezier(...)'s inner commas/spaces don't split the list or tokens.
    // linear-gradient([<angle>|to <side>], stop, stop, …) / radial-gradient([shape,] stop, …).
    // A stop is a colour with an optional position ("#f00 40%"). Returns null if v isn't a gradient.
    private static Gradient? ParseGradient(string v)
    {
        v = v.Trim();
        var lower = v.ToLowerInvariant();
        GradientKind kind;
        if (lower.StartsWith("linear-gradient(") && v.EndsWith(')')) kind = GradientKind.Linear;
        else if (lower.StartsWith("radial-gradient(") && v.EndsWith(')')) kind = GradientKind.Radial;
        else return null;

        var segs = SplitTopLevel(v[(v.IndexOf('(') + 1)..^1], ',');
        if (segs.Count < 2) return null;

        var angle = 180f; // CSS default: to bottom
        var start = 0;
        if (kind == GradientKind.Linear && ParseAngle(segs[0]) is { } a) { angle = a; start = 1; }
        else if (kind == GradientKind.Radial && !IsColorStop(segs[0])) start = 1; // skip shape/size prelude

        var stops = new List<GradientStop>();
        for (var i = start; i < segs.Count; i++)
        {
            SKColor? col = null;
            var pos = float.NaN;
            foreach (var p in SplitTopLevel(segs[i], ' '))
            {
                if (Colors.TryParse(p, out var c)) col = c;
                else if (p.EndsWith('%') && CssNumber.TryParse(p[..^1], out var pct)) pos = pct / 100f;
            }
            if (col is { } cc) stops.Add(new GradientStop(cc, pos));
        }
        return stops.Count >= 2 ? new Gradient(kind, angle, stops) : null;
    }

    private static bool IsColorStop(string seg)
    {
        var toks = SplitTopLevel(seg, ' ');
        return toks.Count > 0 && Colors.TryParse(toks[0], out _);
    }

    // A gradient direction → CSS angle in degrees (0 = to top, 90 = to right). Null if not an angle.
    private static float? ParseAngle(string seg)
    {
        var t = seg.Trim().ToLowerInvariant();
        if (t.EndsWith("deg") && CssNumber.TryParse(t[..^3], out var d)) return d;
        if (t.StartsWith("to "))
            return t[3..].Trim() switch
            {
                "top" => 0f, "right" => 90f, "bottom" => 180f, "left" => 270f,
                "top right" or "right top" => 45f, "bottom right" or "right bottom" => 135f,
                "bottom left" or "left bottom" => 225f, "top left" or "left top" => 315f,
                _ => null,
            };
        return null;
    }

    // box-shadow: [inset] <dx> <dy> [blur] [spread] [color], … (comma-separated layers).
    private static void ParseBoxShadow(ComputedStyle s, string v)
    {
        if (v.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) { s.BoxShadow = null; return; }
        var list = new List<BoxShadow>();
        foreach (var seg in SplitTopLevel(v, ','))
        {
            var inset = false;
            var color = new SKColor(0, 0, 0, 0x40); // default when a layer omits its colour
            var lens = new List<float>();
            foreach (var tok in SplitTopLevel(seg, ' '))
            {
                if (tok.Equals("inset", StringComparison.OrdinalIgnoreCase)) inset = true;
                else if (Colors.TryParse(tok, out var c)) color = c;
                else lens.Add(ParsePx(tok));
            }
            if (lens.Count < 2) continue; // need at least offset-x and offset-y
            list.Add(new BoxShadow(lens[0], lens[1],
                lens.Count > 2 ? MathF.Max(0, lens[2]) : 0f,
                lens.Count > 3 ? lens[3] : 0f, color, inset));
        }
        s.BoxShadow = list.Count > 0 ? list : null;
    }

    private static void ParseTransition(ComputedStyle s, string v)
    {
        var list = new List<TransitionSpec>();
        foreach (var seg in SplitTopLevel(v, ','))
        {
            string prop = "all";
            float dur = 0, delay = 0;
            var times = 0;
            var ease = Easing.Ease; // CSS default timing
            foreach (var tok in SplitTopLevel(seg, ' '))
            {
                if (IsTime(tok, out var secs)) { if (times++ == 0) dur = secs; else delay = secs; }
                else if (ParseEasing(tok) is { } e) ease = e;
                else prop = tok.ToLowerInvariant();
            }
            if (prop == "background-color") prop = "background";
            else if (prop == "border") prop = "border-color";
            list.Add(new TransitionSpec(prop, dur, delay, ease));
        }
        s.Transitions = list;
    }

    // Split on `sep` at the top level only (ignoring separators inside parentheses).
    private static List<string> SplitTopLevel(string s, char sep)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth > 0) depth--; }
            else if (c == sep && depth == 0)
            {
                var t = s[start..i].Trim();
                if (t.Length > 0) parts.Add(t);
                start = i + 1;
            }
        }
        var last = s[start..].Trim();
        if (last.Length > 0) parts.Add(last);
        return parts;
    }

    // A timing function: an easing keyword or a cubic-bezier(x1,y1,x2,y2) literal.
    private static Easing? ParseEasing(string tok)
    {
        var t = tok.Trim().ToLowerInvariant();
        if (Easing.FromKeyword(t) is { } k) return k;
        if (t.StartsWith("cubic-bezier(", StringComparison.Ordinal) && t.EndsWith(')'))
        {
            var nums = t[13..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length == 4
                && CssNumber.TryParse(nums[0], out var x1) && CssNumber.TryParse(nums[1], out var y1)
                && CssNumber.TryParse(nums[2], out var x2) && CssNumber.TryParse(nums[3], out var y2))
                return new Easing(EasingKind.Bezier, Math.Clamp(x1, 0f, 1f), y1, Math.Clamp(x2, 0f, 1f), y2);
        }
        return null;
    }

    private static bool IsTime(string tok, out float seconds)
    {
        seconds = 0f;
        var t = tok.ToLowerInvariant();
        if (t.EndsWith("ms")) { if (CssNumber.TryParse(t[..^2], out var ms)) { seconds = ms / 1000f; return true; } return false; }
        if (t.EndsWith('s') && CssNumber.TryParse(t[..^1], out var sec)) { seconds = sec; return true; }
        return false;
    }

    private static readonly Regex _transformFn = new(@"(\w+)\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex _filterFn = new(@"([\w-]+)\(([^)]*)\)", RegexOptions.Compiled);

    // filter: blur(4px) brightness(1.2) grayscale(50%) drop-shadow(2px 3px 4px #0008) …
    private static void ParseFilter(ComputedStyle s, string v) => s.Filter = ParseFilterOps(v);

    private static List<FilterOp>? ParseFilterOps(string v)
    {
        if (v.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var ops = new List<FilterOp>();
        foreach (Match m in _filterFn.Matches(v))
        {
            var fn = m.Groups[1].Value.ToLowerInvariant();
            var arg = m.Groups[2].Value.Trim();
            switch (fn)
            {
                case "blur": ops.Add(new FilterOp(FilterKind.Blur, ParsePx(arg), 0, 0, default)); break;
                case "brightness": ops.Add(new FilterOp(FilterKind.Brightness, Amount(arg), 0, 0, default)); break;
                case "contrast": ops.Add(new FilterOp(FilterKind.Contrast, Amount(arg), 0, 0, default)); break;
                case "grayscale" or "greyscale": ops.Add(new FilterOp(FilterKind.Grayscale, Amount(arg), 0, 0, default)); break;
                case "saturate": ops.Add(new FilterOp(FilterKind.Saturate, Amount(arg), 0, 0, default)); break;
                case "sepia": ops.Add(new FilterOp(FilterKind.Sepia, Amount(arg), 0, 0, default)); break;
                case "invert": ops.Add(new FilterOp(FilterKind.Invert, Amount(arg), 0, 0, default)); break;
                case "opacity": ops.Add(new FilterOp(FilterKind.Opacity, Amount(arg), 0, 0, default)); break;
                case "drop-shadow":
                {
                    float dx = 0, dy = 0, blur = 0;
                    var col = new SKColor(0, 0, 0, 0x80);
                    var li = 0;
                    foreach (var p in arg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (Colors.TryParse(p, out var c)) col = c;
                        else { var px = ParsePx(p); if (li == 0) dx = px; else if (li == 1) dy = px; else blur = px; li++; }
                    }
                    ops.Add(new FilterOp(FilterKind.DropShadow, dx, dy, blur, col));
                    break;
                }
            }
        }
        return ops.Count > 0 ? ops : null;
    }

    // A filter amount: bare number or percentage (100% → 1.0). Defaults to 1.0.
    private static float Amount(string v)
    {
        v = v.Trim();
        if (v.EndsWith('%')) return CssNumber.TryParse(v[..^1], out var pct) ? pct / 100f : 1f;
        return CssNumber.TryParse(v, out var n) ? n : 1f;
    }

    private static void ParseTransform(ComputedStyle s, string v)
    {
        foreach (Match m in _transformFn.Matches(v))
        {
            var fn = m.Groups[1].Value.ToLowerInvariant();
            var args = m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            float A(int i) => i < args.Length ? ParsePx(args[i]) : 0f;
            float N(int i, float d) => i < args.Length && CssNumber.TryParse(args[i].TrimEnd('d', 'e', 'g'), out var n) ? n : d;
            switch (fn)
            {
                case "translate": s.TranslateX = A(0); s.TranslateY = A(1); s.HasTransform = true; break;
                case "translatex": s.TranslateX = A(0); s.HasTransform = true; break;
                case "translatey": s.TranslateY = A(0); s.HasTransform = true; break;
                case "scale": s.ScaleX = N(0, 1); s.ScaleY = args.Length > 1 ? N(1, 1) : s.ScaleX; s.HasTransform = true; break;
                case "scalex": s.ScaleX = N(0, 1); s.HasTransform = true; break;
                case "scaley": s.ScaleY = N(0, 1); s.HasTransform = true; break;
                case "rotate": s.RotateDeg = N(0, 0); s.HasTransform = true; break;
            }
        }
    }

    /// <summary>
    /// <c>transform-origin: [ left | center | right | &lt;length-percentage&gt; ]
    ///                      [ top | center | bottom | &lt;length-percentage&gt; ]?</c>
    /// — the transform's fixed point. A third (z) value is accepted and ignored; this is a 2D engine.
    /// </summary>
    private static void ParseTransformOrigin(ComputedStyle s, string v)
    {
        var parts = v.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;

        // Keywords are legal in EITHER order (`top left` == `left top`, `bottom center` ==
        // `center bottom`), so detect a swapped pair before assigning positionally. The pair is
        // swapped whenever EITHER end says so: a vertical keyword first, or a horizontal keyword
        // second. Requiring both misread the most common chart form of all, `bottom center` (#63) —
        // its second word names no axis, so it fell through to positional, `bottom` was read as an
        // X of 100%, and the origin came out right-middle: for a scaleY, indistinguishable from
        // unset, which is precisely the symptom #54 had just fixed.
        if (parts.Length >= 2 && (IsVerticalKeyword(parts[0]) || IsHorizontalKeyword(parts[1])))
            (parts[0], parts[1]) = (parts[1], parts[0]);

        // A lone vertical keyword sets Y and leaves X centred — `transform-origin: bottom` is the
        // common bar-chart case, and reading it as an X offset would anchor the wrong axis.
        if (parts.Length == 1 && IsVerticalKeyword(parts[0]))
        {
            s.TransformOriginY = OriginComponent(parts[0]);
            s.TransformOriginX = new Length(LengthUnit.Percent, 50f);
            return;
        }

        s.TransformOriginX = OriginComponent(parts[0]);
        s.TransformOriginY = parts.Length >= 2 ? OriginComponent(parts[1]) : new Length(LengthUnit.Percent, 50f);
    }

    private static bool IsHorizontalKeyword(string p) => p is "left" or "right";
    private static bool IsVerticalKeyword(string p) => p is "top" or "bottom";

    /// <summary>One <c>transform-origin</c> component: an edge/centre keyword, or a length/percentage.</summary>
    private static Length OriginComponent(string p) => p switch
    {
        "left" or "top" => Length.Zero,
        "center" or "centre" => new Length(LengthUnit.Percent, 50f),
        "right" or "bottom" => new Length(LengthUnit.Percent, 100f),
        _ => ParseLen(p),
    };

    private static void ParseFlexShorthand(ComputedStyle s, string v)
    {
        // The keyword forms. Only numbers were understood, so `flex: none` — the ordinary way to say
        // "do not let this shrink" — parsed as nothing at all and left the item shrinking, silently.
        // The repository's own sidebar icons ask for it and were relying on a no-op.
        switch (v.Trim().ToLowerInvariant())
        {
            case "none":                                    // 0 0 auto
                s.FlexGrow = 0; s.FlexShrink = 0; s.FlexBasis = Length.Auto; return;
            case "auto":                                    // 1 1 auto
                s.FlexGrow = 1; s.FlexShrink = 1; s.FlexBasis = Length.Auto; return;
            case "initial":                                 // 0 1 auto
                s.FlexGrow = 0; s.FlexShrink = 1; s.FlexBasis = Length.Auto; return;
        }

        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && CssNumber.TryParse(parts[0], out var g)) { s.FlexGrow = g; s.FlexShrink = 1; s.FlexBasis = new Length(LengthUnit.Px, 0); return; }
        if (parts.Length >= 1 && CssNumber.TryParse(parts[0], out var grow)) s.FlexGrow = grow;
        if (parts.Length >= 2 && CssNumber.TryParse(parts[1], out var shrink)) s.FlexShrink = shrink;
        if (parts.Length >= 3) s.FlexBasis = ParseLen(parts[2]);
    }

    private static void ParseBorderShorthand(ComputedStyle s, string v)
    {
        foreach (var token in v.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase) || CssNumber.TryParse(token, out _))
            { var w = ParsePx(token); s.BorderTop = s.BorderRight = s.BorderBottom = s.BorderLeft = w; }
            else if (ParseBorderStyle(token) is { } st) s.BorderStyle = st;
            else if (Colors.TryParse(token, out var c)) s.BorderColor = c;
        }
    }

    // Supported border-style keywords; hidden→None, unknowns (double/groove/…) fall back to Solid.
    private static BorderLineStyle? ParseBorderStyle(string v) => v.Trim().ToLowerInvariant() switch
    {
        "solid" => BorderLineStyle.Solid,
        "dashed" => BorderLineStyle.Dashed,
        "dotted" => BorderLineStyle.Dotted,
        "none" or "hidden" => BorderLineStyle.None,
        "double" or "groove" or "ridge" or "inset" or "outset" => BorderLineStyle.Solid,
        _ => null,
    };
}
