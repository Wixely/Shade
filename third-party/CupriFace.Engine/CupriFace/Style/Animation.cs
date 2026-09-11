using System.Text.RegularExpressions;
using CupriFace.Dom;

namespace CupriFace.Style;

public sealed record Keyframe(float Offset, Dictionary<string, string> Declarations);

/// <summary>The animatable values of a style before any keyframe touched them. An animation that is
/// not applying a frame — waiting out its delay without a backwards fill, or finished without a
/// forwards fill — puts these back, so seeking to any time gives that time's frame and never the
/// previous call's.</summary>
internal sealed record AnimationBase(float Opacity, bool HasTransform, float TranslateX, float TranslateY,
    float RotateDeg, float ScaleX, float ScaleY, Length Width, Length Height);

/// <summary>
/// Parses <c>@keyframes</c> blocks and applies time-sampled animation overrides to the
/// render tree. Animatable properties: transform + opacity (paint-only), and width +
/// height — those write a definite length that the frame's layout honours, the same road
/// a <c>transition: height</c> takes. Layout always follows Animate (host order:
/// Animate → BuildFrame), so an animated size reflows the element and its siblings.
/// </summary>
public static partial class Animation
{
    [GeneratedRegex(@"@keyframes\s+([A-Za-z_][\w-]*)\s*\{", RegexOptions.Singleline)]
    private static partial Regex KeyframesHeader();

    /// <summary>Extract all @keyframes rules from a stylesheet.</summary>
    public static Dictionary<string, List<Keyframe>> Parse(string? css)
    {
        var map = new Dictionary<string, List<Keyframe>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(css)) return map;

        foreach (Match header in KeyframesHeader().Matches(css))
        {
            var name = header.Groups[1].Value;
            var bodyStart = header.Index + header.Length;
            var body = ExtractBraced(css, bodyStart);
            if (body is null) continue;

            var frames = new List<Keyframe>();
            foreach (Match step in Regex.Matches(body, @"([^{}]+)\{([^{}]*)\}"))
            {
                var decls = CssParser.ParseDeclarations(step.Groups[2].Value);
                foreach (var sel in step.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var offset = sel.Equals("from", StringComparison.OrdinalIgnoreCase) ? 0f
                        : sel.Equals("to", StringComparison.OrdinalIgnoreCase) ? 1f
                        : CssNumber.TryParse(sel.TrimEnd('%'), out var p) ? p / 100f : -1f;
                    if (offset >= 0) frames.Add(new Keyframe(offset, decls));
                }
            }
            frames.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            if (frames.Count > 0) map[name] = frames;
        }
        return map;
    }

    private static string? ExtractBraced(string s, int open)
    {
        var depth = 1;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return s[open..i];
        }
        return null;
    }

    /// <summary>Apply animation overrides to the tree for the given elapsed time. Returns true while
    /// any animation is still RUNNING — a later frame would differ: waiting out its delay, or inside
    /// its iterations. A finished animation (its count of iterations elapsed) holds its last frame if
    /// it fills forwards, otherwise reverts, and either way contributes nothing to the return.</summary>
    public static bool Apply(RenderNode root, Dictionary<string, List<Keyframe>> keyframes, double timeSeconds)
    {
        if (keyframes.Count == 0) return false;
        return Walk(root, keyframes, timeSeconds);
    }

    private static bool Walk(RenderNode node, Dictionary<string, List<Keyframe>> keyframes, double t)
    {
        var running = false;
        var s = node.Style;
        if (s.AnimationName is { } name && s.AnimationDuration > 0 && keyframes.TryGetValue(name, out var frames))
        {
            // The clock is ABSOLUTE document time, not time since the element appeared: the same t
            // gives the same frame whichever order frames are asked for (CupriCut renders out of
            // order; a UI never notices).
            var local = t - s.AnimationDelay;
            if (local < 0)
            {
                if (s.AnimationFillBackwards) ApplyFrame(s, frames, 0f); else Restore(s);
                running = true;
            }
            else
            {
                var cycles = local / s.AnimationDuration;
                if (cycles >= s.AnimationIterations)
                {
                    if (s.AnimationFillForwards) ApplyFrame(s, frames, 1f); else Restore(s);
                }
                else
                {
                    ApplyFrame(s, frames, (float)(cycles % 1.0));
                    running = true;
                }
            }
        }
        foreach (var c in node.Children) if (Walk(c, keyframes, t)) running = true;
        return running;
    }

    private static void Restore(ComputedStyle s)
    {
        if (s.AnimBase is not { } b) return;
        s.Opacity = b.Opacity; s.HasTransform = b.HasTransform;
        s.TranslateX = b.TranslateX; s.TranslateY = b.TranslateY; s.RotateDeg = b.RotateDeg;
        s.ScaleX = b.ScaleX; s.ScaleY = b.ScaleY; s.Width = b.Width; s.Height = b.Height;
    }

    private static void ApplyFrame(ComputedStyle s, List<Keyframe> frames, float progress)
    {
        s.AnimBase ??= new AnimationBase(s.Opacity, s.HasTransform, s.TranslateX, s.TranslateY, s.RotateDeg, s.ScaleX, s.ScaleY, s.Width, s.Height);
        // Find bracketing keyframes.
        Keyframe a = frames[0], b = frames[^1];
        for (var i = 0; i < frames.Count - 1; i++)
        {
            if (progress >= frames[i].Offset && progress <= frames[i + 1].Offset)
            {
                a = frames[i]; b = frames[i + 1]; break;
            }
        }
        var span = b.Offset - a.Offset;
        var local = span > 0 ? (progress - a.Offset) / span : 0f;

        var from = new ComputedStyle();
        var to = new ComputedStyle();
        StyleResolver.ApplyDeclarations(from, a.Declarations);
        StyleResolver.ApplyDeclarations(to, b.Declarations);

        if (from.HasTransform || to.HasTransform)
        {
            s.HasTransform = true;
            s.TranslateX = Lerp(from.TranslateX, to.TranslateX, local);
            s.TranslateY = Lerp(from.TranslateY, to.TranslateY, local);
            s.RotateDeg = Lerp(from.RotateDeg, to.RotateDeg, local);
            s.ScaleX = Lerp(from.ScaleX, to.ScaleX, local);
            s.ScaleY = Lerp(from.ScaleY, to.ScaleY, local);
        }
        if (a.Declarations.ContainsKey("opacity") || b.Declarations.ContainsKey("opacity"))
            s.Opacity = Lerp(from.Opacity, to.Opacity, local);

        // Layout properties: the declarations were already parsed into `from`/`to` (width included) —
        // they were just never read. Write the interpolated value as a definite length and the frame's
        // layout picks it up; this was the gap that held a keyframed bar at its start width for the
        // whole run while the engine reported the animation active (#56).
        if (a.Declarations.ContainsKey("width") || b.Declarations.ContainsKey("width"))
            s.Width = LerpLength(from.Width, to.Width, local);
        if (a.Declarations.ContainsKey("height") || b.Declarations.ContainsKey("height"))
            s.Height = LerpLength(from.Height, to.Height, local);
    }

    // Same-unit px or % pairs interpolate; anything else — auto (an endpoint that omitted the
    // property), mixed units — flips at the midpoint, which is CSS's behaviour for a
    // non-interpolable pair. A % stays a % and resolves in layout, where the containing block is
    // actually known.
    private static Length LerpLength(Length a, Length b, float t)
    {
        if (a.Unit == b.Unit && a.Unit is LengthUnit.Px or LengthUnit.Percent)
            return new Length(a.Unit, Lerp(a.Value, b.Value, t));
        return t < 0.5f ? a : b;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
