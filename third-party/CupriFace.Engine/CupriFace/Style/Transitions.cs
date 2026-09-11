using System.Text;
using CupriFace.Dom;
using SkiaSharp;

namespace CupriFace.Style;

/// <summary>The properties a CSS <c>transition</c> can animate, read/written on <see cref="ComputedStyle"/>.
/// Most are paint-only (a transition re-paints without re-laying-out, like <c>@keyframes</c>); the
/// exceptions are <see cref="TransProp.Height"/> and <see cref="TransProp.Width"/>, written as a definite
/// size before layout so the element (and everything around it) reflows each frame — a real layout
/// animation (collapse/expand a panel, or a sidebar).</summary>
public enum TransProp { Opacity, Background, Color, BorderColor, Transform, Filter, Height, Width }

public enum EasingKind { Linear, Bezier }

/// <summary>A CSS timing function. Keywords (<c>ease</c>/<c>ease-in</c>/…) resolve to their standard
/// cubic-bézier control points; <c>linear</c> is a fast path. (<c>cubic-bezier()</c> literals are a
/// follow-up — the comma parsing collides with the transition-list separator.)</summary>
public readonly record struct Easing(EasingKind Kind, float X1, float Y1, float X2, float Y2)
{
    public static readonly Easing Linear = new(EasingKind.Linear, 0, 0, 1, 1);
    public static readonly Easing Ease = new(EasingKind.Bezier, 0.25f, 0.1f, 0.25f, 1f);
    public static readonly Easing EaseIn = new(EasingKind.Bezier, 0.42f, 0f, 1f, 1f);
    public static readonly Easing EaseOut = new(EasingKind.Bezier, 0f, 0f, 0.58f, 1f);
    public static readonly Easing EaseInOut = new(EasingKind.Bezier, 0.42f, 0f, 0.58f, 1f);

    public static Easing? FromKeyword(string k) => k switch
    {
        "linear" => Linear,
        "ease" => Ease,
        "ease-in" => EaseIn,
        "ease-out" => EaseOut,
        "ease-in-out" => EaseInOut,
        _ => null,
    };

    /// <summary>Map animation progress <paramref name="t"/> (0..1 in time) to eased progress
    /// (0..1 in value): solve x(s)=t on the bézier, then return y(s).</summary>
    public float Eval(float t)
    {
        if (Kind == EasingKind.Linear || t <= 0f || t >= 1f) return Math.Clamp(t, 0f, 1f);
        var s = t; // Newton-Raphson on x(s) = t (a few iterations converge for CSS curves)
        for (var i = 0; i < 6; i++)
        {
            var dx = Deriv(s, X1, X2);
            if (MathF.Abs(dx) < 1e-6f) break;
            s = Math.Clamp(s - (Curve(s, X1, X2) - t) / dx, 0f, 1f);
        }
        return Curve(s, Y1, Y2);
    }

    // Cubic bézier with endpoints P0=0, P3=1 and control values p1,p2.
    private static float Curve(float t, float p1, float p2)
    {
        float mt = 1 - t;
        return 3 * mt * mt * t * p1 + 3 * mt * t * t * p2 + t * t * t;
    }
    private static float Deriv(float t, float p1, float p2)
    {
        float mt = 1 - t;
        return 3 * mt * mt * p1 + 6 * mt * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }
}

/// <summary>One parsed <c>transition</c> entry: which property (or <c>all</c>), how long, after what
/// delay, with what easing.</summary>
public readonly record struct TransitionSpec(string Property, float Duration, float Delay, Easing Easing);

/// <summary>
/// Drives CSS <c>transition</c>s: when a transitioned property's target value changes between style
/// resolutions (hover, class/model change, focus), it animates from the currently-displayed value to
/// the new target over the declared duration/easing. State is keyed by structural path (like scroll
/// state) so it survives the per-interaction rebuild that re-parses the DOM.
/// </summary>
public sealed class TransitionEngine
{
    private sealed class St
    {
        public float[] From = [], To = [], Current = [];
        // Filter is structured (a list of ops), so it gets its own endpoints instead of the float[].
        public List<FilterOp>? FromFil, ToFil, CurFil;
        public bool FilterInterp; // endpoints have matching op-kinds → interpolate; else discrete (jump at 50%)
        public double Start = double.NaN; // set on the first applied frame (NaN = pending)
        public float Duration, Delay;
        public Easing Easing;
        public bool Active;
        public bool Seen;
    }

    private static readonly TransProp[] AllProps = [TransProp.Opacity, TransProp.Background, TransProp.Color, TransProp.BorderColor, TransProp.Transform, TransProp.Filter, TransProp.Height, TransProp.Width];
    private readonly Dictionary<string, St> _states = new();

    /// <summary>True while any transition is mid-flight (drives the host's continuous repaint).</summary>
    public bool Active { get; private set; }

    private float _vw, _vh; // viewport, for percent targets on position:fixed nodes

    /// <summary>After a style resolution: diff each transitioned property's target against the last one
    /// and (re)start a transition where it changed. Prunes state for nodes that vanished.
    /// <paramref name="viewportW"/>/<paramref name="viewportH"/>: what a FIXED node's percentage
    /// width/height resolves against — its parent's content box is the wrong containing block for
    /// it (the bug: fullscreening a video with a width transition animated it to the card's width
    /// and pinned it there).</summary>
    public void Detect(RenderNode root, float viewportW, float viewportH)
    {
        _vw = viewportW; _vh = viewportH;
        foreach (var st in _states.Values) st.Seen = false;
        Walk(root);
        List<string>? drop = null;
        foreach (var (k, st) in _states) if (!st.Seen) (drop ??= []).Add(k);
        if (drop is not null) foreach (var k in drop) _states.Remove(k);
        Recompute();
    }

    private void Walk(RenderNode n)
    {
        if (n.Style.Transitions is { Count: > 0 } specs)
        {
            var path = PathOf(n);
            foreach (var p in AllProps)
            {
                if (Resolve(specs, p) is not { } spec) continue;
                var key = $"{path}|{(int)p}";

                if (p == TransProp.Filter) { DetectFilter(n.Style, key, spec); continue; }
                if (p == TransProp.Height) { DetectHeight(n, key, spec); continue; }
                if (p == TransProp.Width) { DetectWidth(n, key, spec); continue; }

                var target = Read(n.Style, p);
                if (_states.TryGetValue(key, out var st))
                {
                    st.Seen = true;
                    st.Duration = spec.Duration; st.Delay = spec.Delay; st.Easing = spec.Easing;
                    if (!Approx(target, st.To)) // target changed → animate from the current displayed value
                    {
                        st.From = st.Current; st.To = target; st.Start = double.NaN;
                        st.Active = spec.Duration > 0;
                        if (!st.Active) st.Current = target;
                    }
                }
                else // first sight of this property → baseline, no animation on initial style
                {
                    _states[key] = new St
                    {
                        From = target, To = target, Current = target, Seen = true,
                        Duration = spec.Duration, Delay = spec.Delay, Easing = spec.Easing,
                    };
                }
            }
        }
        foreach (var c in n.Children) Walk(c);
    }

    /// <summary>Each frame before paint: write the interpolated value of every in-flight transition
    /// onto its node's style. Returns whether anything is still animating.</summary>
    public bool Apply(RenderNode root, double now)
    {
        if (_states.Count > 0) ApplyWalk(root, now);
        Recompute();
        return Active;
    }

    private void ApplyWalk(RenderNode n, double now)
    {
        if (n.Style.Transitions is { Count: > 0 })
        {
            var path = PathOf(n);
            foreach (var p in AllProps)
            {
                if (!_states.TryGetValue($"{path}|{(int)p}", out var st) || !st.Active) continue;
                if (double.IsNaN(st.Start)) st.Start = now;
                var u = st.Duration > 0 ? (float)((now - st.Start - st.Delay) / st.Duration) : 1f;

                if (p == TransProp.Filter) { ApplyFilter(n.Style, st, u); continue; }

                if (u <= 0f) { WriteProp(n, p, st.From); continue; }        // delay phase: hold the start value
                if (u >= 1f) { st.Current = st.To; st.Active = false; WriteProp(n, p, st.To); continue; }
                st.Current = Lerp(st.From, st.To, st.Easing.Eval(u));
                WriteProp(n, p, st.Current);
            }
        }
        foreach (var c in n.Children) ApplyWalk(c, now);
    }

    private void Recompute()
    {
        Active = false;
        foreach (var st in _states.Values) if (st.Active) { Active = true; break; }
    }

    // The effective spec for a property: the last matching entry (an explicit name or `all`) wins.
    private static TransitionSpec? Resolve(List<TransitionSpec> specs, TransProp p)
    {
        var name = Name(p);
        TransitionSpec? found = null;
        foreach (var sp in specs) if (sp.Property == name || sp.Property == "all") found = sp;
        return found;
    }

    private static string Name(TransProp p) => p switch
    {
        TransProp.Opacity => "opacity",
        TransProp.Background => "background",
        TransProp.Color => "color",
        TransProp.BorderColor => "border-color",
        TransProp.Filter => "filter",
        TransProp.Height => "height",
        TransProp.Width => "width",
        _ => "transform",
    };

    // Width transitions animate a definite width (px/%): a target change animates from the current
    // interpolated value, like the paint props. `auto` (shrink-to-fit) has no measured natural width, so
    // it snaps rather than animating — kept simple because layout collapses (sidebars, rails) use a
    // definite collapsed width. Written before layout, so the element and its neighbours reflow.
    private void DetectWidth(RenderNode n, string key, TransitionSpec spec)
    {
        if (n.Style.Width.IsAuto)
        {
            if (_states.TryGetValue(key, out var s0)) { s0.Seen = true; s0.Duration = spec.Duration; s0.Delay = spec.Delay; s0.Easing = spec.Easing; }
            return;
        }
        var cbWidth = n.Style.Position == PositionType.Fixed ? _vw : n.Parent?.ContentBoxWidth ?? 0f;
        var target = n.Style.Width.Resolve(cbWidth);
        if (_states.TryGetValue(key, out var st))
        {
            st.Seen = true;
            st.Duration = spec.Duration; st.Delay = spec.Delay; st.Easing = spec.Easing;
            var to = st.To.Length > 0 ? st.To[0] : target;
            if (MathF.Abs(target - to) > 0.5f) // target changed → animate from the current displayed width
            {
                st.From = st.Current.Length > 0 ? st.Current : [target];
                st.To = [target]; st.Start = double.NaN;
                st.Active = spec.Duration > 0;
                if (!st.Active) st.Current = [target];
            }
        }
        else // first sight → baseline at the current width, no animation
        {
            _states[key] = new St
            {
                From = [target], To = [target], Current = [target], Seen = true,
                Duration = spec.Duration, Delay = spec.Delay, Easing = spec.Easing,
            };
        }
    }

    // The px height layout would give this node: an explicit height as-is, or (for `auto`) the measured
    // natural height from the last layout — so a transition can animate a collapse/expand to/from auto.
    private float HeightTarget(RenderNode n)
    {
        var h = n.Style.Height;
        var cb = n.Style.Position == PositionType.Fixed ? _vh : n.Parent?.ContentBoxHeight ?? 0f;
        return h.IsAuto ? n.ContentNaturalHeight : h.Resolve(cb, n.ContentNaturalHeight);
    }

    // Height is driven off the laid-out height, not a style value: animate whenever the target layout
    // wants (explicit height, or measured natural for auto) differs from what's actually on screen
    // (n.PrevHeight, carried across the rebuild). Detecting against the displayed height — not the stored
    // target — makes a collapse/expand animate every time, including the first collapse of an element
    // that started expanded (whose natural height wasn't measurable at its first, pre-layout, detect).
    private void DetectHeight(RenderNode n, string key, TransitionSpec spec)
    {
        var displayed = n.PrevHeight;
        var target = HeightTarget(n);
        if (_states.TryGetValue(key, out var st))
        {
            st.Seen = true;
            st.Duration = spec.Duration; st.Delay = spec.Delay; st.Easing = spec.Easing;
            var to = st.To.Length > 0 ? st.To[0] : target;
            var retarget = MathF.Abs(target - to) > 0.5f;                 // layout wants a different height
            // displayed > 0 guard: a node detected before its FIRST layout reads 0 — without it,
            // every definite-height element with a height transition entrance-animated from zero
            // on load (16 full-page reflow frames before the video's fast path could engage).
            var divergedIdle = !st.Active && displayed > 0.5f && MathF.Abs(displayed - target) > 0.5f;
            if (retarget || divergedIdle)
            {
                st.From = [displayed]; st.To = [target]; st.Start = double.NaN;
                st.Active = spec.Duration > 0 && MathF.Abs(displayed - target) > 0.5f;
                if (!st.Active) st.Current = [target];
            }
        }
        else // first sight → baseline at the current target, no animation
        {
            _states[key] = new St
            {
                From = [target], To = [target], Current = [target], Seen = true,
                Duration = spec.Duration, Delay = spec.Delay, Easing = spec.Easing,
            };
        }
    }

    // Write an interpolated value onto the node's style. Height becomes a definite px height that layout
    // then honours (reflowing the element and its siblings); the rest are the paint-only Write below.
    private static void WriteProp(RenderNode n, TransProp p, float[] v)
    {
        if (p == TransProp.Height) n.Style.Height = new Length(LengthUnit.Px, MathF.Max(0f, v[0]));
        else if (p == TransProp.Width) n.Style.Width = new Length(LengthUnit.Px, MathF.Max(0f, v[0]));
        else Write(n.Style, p, v);
    }

    // ---- filter transitions (structured: a list of ops, interpolated op-by-op) --------------------

    private void DetectFilter(ComputedStyle style, string key, TransitionSpec spec)
    {
        var target = style.Filter; // List<FilterOp>? (null = no filter)
        if (_states.TryGetValue(key, out var st))
        {
            st.Seen = true;
            st.Duration = spec.Duration; st.Delay = spec.Delay; st.Easing = spec.Easing;
            if (!FilterEqual(target, st.ToFil))
            {
                st.FromFil = st.CurFil ?? st.ToFil; // animate from the currently-displayed filter
                st.ToFil = target;
                st.FilterInterp = FilterInterpolable(st.FromFil, st.ToFil);
                st.Start = double.NaN;
                st.Active = spec.Duration > 0;
                if (!st.Active) st.CurFil = target;
            }
        }
        else // baseline — no animation on the initial style
        {
            _states[key] = new St
            {
                FromFil = target, ToFil = target, CurFil = target, Seen = true,
                Duration = spec.Duration, Delay = spec.Delay, Easing = spec.Easing,
            };
        }
    }

    private static void ApplyFilter(ComputedStyle style, St st, float u)
    {
        if (u <= 0f) { style.Filter = st.FromFil; return; }               // delay phase: hold the start
        if (u >= 1f) { st.CurFil = st.ToFil; st.Active = false; style.Filter = st.ToFil; return; }
        var e = st.Easing.Eval(u);
        if (!st.FilterInterp) { style.Filter = e < 0.5f ? st.FromFil : st.ToFil; return; } // discrete
        st.CurFil = LerpFilters(st.FromFil, st.ToFil, e);
        style.Filter = st.CurFil;
    }

    // The identity (no-op) value for a filter function — what a missing side interpolates from/to.
    private static float Identity(FilterKind k) => k switch
    {
        FilterKind.Brightness or FilterKind.Contrast or FilterKind.Saturate or FilterKind.Opacity => 1f,
        _ => 0f, // blur, grayscale, sepia, invert, drop-shadow amounts start at 0
    };

    // Pad a (possibly null/empty) list to match a reference list's op-kinds, using identity amounts.
    private static List<FilterOp> PadToKinds(List<FilterOp>? list, List<FilterOp> kinds)
    {
        var outp = new List<FilterOp>(kinds.Count);
        for (var i = 0; i < kinds.Count; i++)
        {
            if (list is not null && i < list.Count && list[i].Kind == kinds[i].Kind) outp.Add(list[i]);
            else outp.Add(new FilterOp(kinds[i].Kind, Identity(kinds[i].Kind), 0, 0, new SKColor(0, 0, 0, 0)));
        }
        return outp;
    }

    // Two filter chains interpolate if one is empty (→ identity of the other) or their op-kinds match.
    private static bool FilterInterpolable(List<FilterOp>? a, List<FilterOp>? b)
    {
        var an = a is { Count: > 0 }; var bn = b is { Count: > 0 };
        if (!an || !bn) return true; // empty side pads to identities of the other
        if (a!.Count != b!.Count) return false;
        for (var i = 0; i < a.Count; i++) if (a[i].Kind != b[i].Kind) return false;
        return true;
    }

    private static List<FilterOp> LerpFilters(List<FilterOp>? from, List<FilterOp>? to, float t)
    {
        var kinds = to is { Count: > 0 } ? to : from ?? [];
        var f = PadToKinds(from, kinds);
        var g = PadToKinds(to, kinds);
        var outp = new List<FilterOp>(kinds.Count);
        for (var i = 0; i < kinds.Count; i++)
        {
            var a = f[i]; var b = g[i];
            outp.Add(new FilterOp(a.Kind,
                a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t, a.C + (b.C - a.C) * t,
                LerpColor(a.Color, b.Color, t)));
        }
        return outp;
    }

    private static bool FilterEqual(List<FilterOp>? a, List<FilterOp>? b)
    {
        var an = a is { Count: > 0 }; var bn = b is { Count: > 0 };
        if (!an && !bn) return true;
        if (an != bn || a!.Count != b!.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.Kind != y.Kind || MathF.Abs(x.A - y.A) > 0.001f || MathF.Abs(x.B - y.B) > 0.001f
                || MathF.Abs(x.C - y.C) > 0.001f || x.Color != y.Color) return false;
        }
        return true;
    }

    private static SKColor LerpColor(SKColor a, SKColor b, float t) => new(
        Byte(a.Red + (b.Red - a.Red) * t), Byte(a.Green + (b.Green - a.Green) * t),
        Byte(a.Blue + (b.Blue - a.Blue) * t), Byte(a.Alpha + (b.Alpha - a.Alpha) * t));

    private static float[] Read(ComputedStyle s, TransProp p) => p switch
    {
        TransProp.Opacity => [s.Opacity],
        TransProp.Background => Channels(s.Background),
        TransProp.Color => Channels(s.Color),
        TransProp.BorderColor => Channels(s.BorderColor),
        _ => [s.TranslateX, s.TranslateY, s.ScaleX, s.ScaleY, s.RotateDeg],
    };

    private static void Write(ComputedStyle s, TransProp p, float[] v)
    {
        switch (p)
        {
            case TransProp.Opacity: s.Opacity = Math.Clamp(v[0], 0f, 1f); break;
            case TransProp.Background: s.Background = Color(v); break;
            case TransProp.Color: s.Color = Color(v); break;
            case TransProp.BorderColor: s.BorderColor = Color(v); break;
            default:
                s.TranslateX = v[0]; s.TranslateY = v[1]; s.ScaleX = v[2]; s.ScaleY = v[3]; s.RotateDeg = v[4];
                s.HasTransform = v[0] != 0 || v[1] != 0 || v[2] != 1f || v[3] != 1f || v[4] != 0;
                break;
        }
    }

    private static float[] Channels(SKColor c) => [c.Red, c.Green, c.Blue, c.Alpha];
    private static SKColor Color(float[] v) => new(Byte(v[0]), Byte(v[1]), Byte(v[2]), Byte(v[3]));
    private static byte Byte(float f) => (byte)Math.Clamp((int)MathF.Round(f), 0, 255);

    private static float[] Lerp(float[] a, float[] b, float t)
    {
        var r = new float[a.Length];
        for (var i = 0; i < a.Length; i++) r[i] = a[i] + (b[i] - a[i]) * t;
        return r;
    }
    private static bool Approx(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (MathF.Abs(a[i] - b[i]) > 0.001f) return false;
        return true;
    }

    // Structural path from the root (child-index chain) — stable across the DOM re-parse, matching
    // how scroll/resize state is keyed in CupriDocument.
    private static string PathOf(RenderNode n)
    {
        var sb = new StringBuilder();
        for (var cur = n; cur.Parent is { } p; cur = p) sb.Insert(0, "/" + p.Children.IndexOf(cur));
        return sb.ToString();
    }
}
