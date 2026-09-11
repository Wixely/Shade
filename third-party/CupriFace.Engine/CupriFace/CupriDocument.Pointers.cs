using AngleSharp.Dom;
using CupriFace.Interaction;

namespace CupriFace;

/// <summary>
/// Multi-pointer input. The engine's own gestures — tap, scroll, fling, long-press, drag surfaces —
/// are single-pointer by design, and the built-in components stay that way so they keep their
/// keyboard and screen-reader behaviour. This is the seam for everything else: an author who wants
/// pinch-to-zoom, two-finger rotate, a collage tile that scales under two fingers, or simply two
/// sliders moved at once gets the raw pointers and decides what they mean.
///
/// The model is the web's, not a bespoke one: <b>a pointer is captured by a target on down and
/// stays with it</b> until it lifts. Capture is what keeps a gesture from fighting the scroller
/// underneath — once an element owns a finger, the recognizer never sees it — which is why there is
/// no <c>touch-action</c> arbitration here yet. It would be solving a problem capture already
/// solved.
///
/// Accessibility is explicitly the author's business here. A pinch has no keyboard equivalent and
/// no screen-reader affordance unless someone writes one; the engine does not pretend otherwise,
/// and TOOLBOX says so plainly.
/// </summary>
public sealed partial class CupriDocument
{
    private readonly List<(string Attribute, Func<MultiPointerEvent, bool> Handler)> _pointerHandlers = new();

    // Captured pointers, keyed by pointer id → the STRUCTURAL PATH of the element that owns it.
    // A path, not a node: the tree is rebuilt constantly (every keystroke), and a node reference
    // captured on finger-down would dangle before the finger lifts.
    private readonly Dictionary<int, (string Path, string Attribute)> _captured = new();
    private readonly Dictionary<int, (float X, float Y)> _active = new();

    /// <summary>Register a handler for elements carrying <paramref name="dataAttribute"/>. Shaped
    /// like <see cref="OnAction"/>: the attribute is the opt-in, so nothing becomes multi-touch by
    /// accident. Returning true consumes the pointer for that element (it is captured); returning
    /// false on the DOWN phase declines, and the pointer falls through to the ordinary gesture
    /// recognizer as if the handler were not there.</summary>
    public CupriDocument OnPointer(string dataAttribute, Func<MultiPointerEvent, bool> handler)
    {
        _pointerHandlers.Add((dataAttribute, handler));
        return this;
    }

    /// <summary>Recognise a drag / pinch / rotate on elements carrying
    /// <paramref name="dataAttribute"/>, instead of handling raw pointers yourself. Built ON TOP of
    /// <see cref="OnPointer"/> — same attribute opt-in, same capture, no new rules — so this is a
    /// convenience, not a different system. Raw pointers remain available for anything this does
    /// not describe.
    ///
    /// What it saves you is not the trigonometry but the mistakes in it: the focal point (a pinch
    /// scales about the midpoint BETWEEN the fingers, not the element's centre), and re-baselining
    /// when a finger joins or leaves so the cumulative values do not jump mid-gesture.
    ///
    /// State is keyed by the ATTRIBUTE'S VALUE, not the element: the tree is rebuilt while you are
    /// still holding the gesture, so an element reference would not survive its own drag. Give each
    /// manipulable element a distinct value.</summary>
    public CupriDocument OnManipulate(string dataAttribute, Func<ManipulationEvent, bool> handler)
    {
        var states = new Dictionary<string, Manip>();

        return OnPointer(dataAttribute, e =>
        {
            var key = e.Value.Length > 0 ? e.Value : dataAttribute;
            if (!states.TryGetValue(key, out var m)) states[key] = m = new Manip();

            var (fx, fy) = Centre(e.Pointers);
            var span = Spread(e.Pointers);
            var angle = Angle(e.Pointers);

            // A finger arriving or leaving changes what span and angle MEAN, so fold what has
            // happened so far into the accumulators and start measuring afresh from here. Without
            // this, lifting one of three fingers makes the content jump.
            if (e.Phase == PointerPhase.Down || e.Pointers.Count != m.Count)
            {
                m.AccumScale *= m.LiveScale;
                m.AccumRotation += m.LiveRotation;
                m.AccumPanX += m.LivePanX;
                m.AccumPanY += m.LivePanY;
                m.LiveScale = 1; m.LiveRotation = 0; m.LivePanX = 0; m.LivePanY = 0;
                m.BaseSpan = span; m.BaseAngle = angle; m.BaseX = fx; m.BaseY = fy;
                m.Count = e.Pointers.Count;
            }
            else
            {
                // One finger can only pan; two or more also scale and turn.
                m.LivePanX = fx - m.BaseX;
                m.LivePanY = fy - m.BaseY;
                if (e.Pointers.Count >= 2 && m.BaseSpan > 0.01)
                {
                    m.LiveScale = span / m.BaseSpan;
                    m.LiveRotation = Normalise(angle - m.BaseAngle);
                }
            }

            var changed = handler(new ManipulationEvent(
                Scale: m.AccumScale * m.LiveScale,
                Rotation: m.AccumRotation + m.LiveRotation,
                PanX: m.AccumPanX + m.LivePanX,
                PanY: m.AccumPanY + m.LivePanY,
                FocusX: fx, FocusY: fy,
                PointerCount: e.Pointers.Count,
                Phase: e.Phase, Element: e.Element, Value: e.Value, Model: _model));

            if (e.Phase is PointerPhase.Up or PointerPhase.Cancel && e.Pointers.Count <= 1)
                states.Remove(key);          // the last finger left: the next touch starts over
            return changed;
        });
    }

    private sealed class Manip
    {
        public double AccumScale = 1, AccumRotation, AccumPanX, AccumPanY;
        public double LiveScale = 1, LiveRotation, LivePanX, LivePanY;
        public double BaseSpan, BaseAngle, BaseX, BaseY;
        public int Count;
    }

    private static (double X, double Y) Centre(IReadOnlyList<CupriPointer> pointers)
    {
        if (pointers.Count == 0) return (0, 0);
        double x = 0, y = 0;
        foreach (var p in pointers) { x += p.X; y += p.Y; }
        return (x / pointers.Count, y / pointers.Count);
    }

    // Mean distance from the centre — a definition that keeps working with three fingers, where
    // "the distance between the two" has no meaning.
    private static double Spread(IReadOnlyList<CupriPointer> pointers)
    {
        if (pointers.Count < 2) return 0;
        var (cx, cy) = Centre(pointers);
        double total = 0;
        foreach (var p in pointers) total += Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
        return total / pointers.Count;
    }

    private static double Angle(IReadOnlyList<CupriPointer> pointers) =>
        pointers.Count < 2 ? 0
        : Math.Atan2(pointers[1].Y - pointers[0].Y, pointers[1].X - pointers[0].X) * 180 / Math.PI;

    /// <summary>Keep a turn on the short side of the circle: crossing the ±180° seam must read as a
    /// few degrees, not most of a revolution.</summary>
    private static double Normalise(double degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees < -180) degrees += 360;
        return degrees;
    }

    /// <summary>True while any pointer is captured — the host's cue that its single-pointer gesture
    /// recognizer should stay out of the way.</summary>
    public bool HasCapturedPointers => _captured.Count > 0;

    /// <summary>Whether this particular pointer belongs to an element rather than to the engine.</summary>
    public bool IsPointerCaptured(int pointerId) => _captured.ContainsKey(pointerId);

    /// <summary>Feed one pointer. Returns true when an element owns it — the caller must then NOT
    /// give that pointer to the ordinary touch recognizer.</summary>
    public bool DispatchPointer(int pointerId, PointerPhase phase, float xHost, float yHost)
    {
        EnsureLaidOut();
        // Host-logical → document, like every other entry point: a pinch on a zoomed page must
        // still address the element under the fingers.
        float x = Zc(xHost), y = Zc(yHost);

        if (phase is PointerPhase.Down)
        {
            _active[pointerId] = (x, y);
            var (node, attribute, handler) = FindPointerTarget(x, y);
            if (node is null)
            {
                _active.Remove(pointerId);
                // Nobody opted in here — but a finger the page itself owns is exactly what page
                // zoom is made of, so remember it. It is only CONSUMED once a second one joins;
                // one finger must still tap, scroll and fling as it always did.
                return TrackPageFinger(pointerId, phase, xHost, yHost);
            }

            // The handler may decline on down (a tile that only reacts to a SECOND finger, say),
            // in which case the pointer is never captured and the recognizer takes it.
            var path = PathOf(node);
            // Capture BEFORE invoking, so the handler's view of "pointers on this element" already
            // includes the one that just arrived.
            _captured[pointerId] = (path, attribute);
            if (Invoke(handler, node.Element!, attribute, path, pointerId, phase, x, y)) return true;
            _captured.Remove(pointerId);                 // declined: the recognizer may have it
            _active.Remove(pointerId);
            return false;
        }

        if (!_captured.TryGetValue(pointerId, out var capture))
            return TrackPageFinger(pointerId, phase, xHost, yHost);

        _active[pointerId] = (x, y);
        if (NodeAtPath(capture.Path)?.Element is { } el && FindHandler(capture.Attribute) is { } h)
            Invoke(h, el, capture.Attribute, capture.Path, pointerId, phase, x, y);

        if (phase is PointerPhase.Up or PointerPhase.Cancel)
        {
            _captured.Remove(pointerId);
            _active.Remove(pointerId);
        }
        // Consumed whatever the handler answered: a captured pointer belongs to that element until
        // it lifts, or one indecisive frame would hand a half-finished gesture to the scroller.
        return true;
    }

    // ---- page zoom gesture ---------------------------------------------------------------------
    // Fingers no element captured. Kept in HOST coordinates: this gesture measures the distance
    // between two fingers on the glass, which must not itself change as the zoom it is driving
    // changes the document scale — measuring in document space would feed the gesture its own
    // output and run away.
    private readonly Dictionary<int, (float X, float Y)> _pageFingers = new();
    private float _pinchStartSpan, _pinchStartZoom;

    /// <summary>Two uncaptured fingers zoom the whole page. On by default, as it is in a browser —
    /// an accessibility affordance nobody switches on helps nobody. An app that owns the gesture
    /// itself (a map, a canvas) sets this false.</summary>
    public bool PageZoomEnabled { get; set; } = true;

    /// <summary>True while a page-zoom pinch is in flight, so a host can cancel the single-pointer
    /// gesture it had started — otherwise the page scrolls under the fingers while they pinch.</summary>
    public bool PageZoomActive => _pinchStartSpan > 0;

    private bool TrackPageFinger(int id, PointerPhase phase, float xHost, float yHost)
    {
        if (!PageZoomEnabled) return false;

        if (phase is PointerPhase.Up or PointerPhase.Cancel)
        {
            _pageFingers.Remove(id);
            if (_pageFingers.Count < 2) _pinchStartSpan = 0;   // the pinch is over
            return false;                                      // never consume a lift
        }

        _pageFingers[id] = (xHost, yHost);
        if (_pageFingers.Count < 2) return false;              // one finger is not a pinch

        var span = Span();
        if (_pinchStartSpan <= 0)
        {
            // A pinch begins. Bank where it started so the whole gesture is measured from one
            // baseline — accumulating per-move ratios would drift.
            _pinchStartSpan = span;
            _pinchStartZoom = Zoom;
            return true;
        }

        if (span > 0.01f) Zoom = _pinchStartZoom * (span / _pinchStartSpan);
        return true;
    }

    private float Span()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var (fx, fy) in _pageFingers.Values)
        {
            minX = MathF.Min(minX, fx); maxX = MathF.Max(maxX, fx);
            minY = MathF.Min(minY, fy); maxY = MathF.Max(maxY, fy);
        }
        float dx = maxX - minX, dy = maxY - minY;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Drop every captured pointer — the window lost focus, the app was backgrounded, the
    /// surface went away. Handlers hear Cancel so a half-finished gesture can unwind itself.</summary>
    public void CancelPointers()
    {
        foreach (var (id, capture) in _captured.ToList())
        {
            var pos = _active.TryGetValue(id, out var p) ? p : (X: 0f, Y: 0f);
            if (NodeAtPath(capture.Path)?.Element is { } el && FindHandler(capture.Attribute) is { } h)
                Invoke(h, el, capture.Attribute, capture.Path, id, PointerPhase.Cancel, pos.X, pos.Y);
        }
        _captured.Clear();
        _active.Clear();
    }

    private Func<MultiPointerEvent, bool>? FindHandler(string attribute)
    {
        foreach (var (attr, handler) in _pointerHandlers)
            if (attr == attribute) return handler;
        return null;
    }

    private (Dom.RenderNode? Node, string Attribute, Func<MultiPointerEvent, bool> Handler) FindPointerTarget(float x, float y)
    {
        for (var n = HitTesting.HitTest(_root, x, y); n is not null; n = n.Parent)
        {
            if (n.Element is not { } el) continue;
            foreach (var (attr, handler) in _pointerHandlers)
                if (el.HasAttribute(attr)) return (n, attr, handler);
        }
        return (null, "", _ => false);
    }

    private bool Invoke(Func<MultiPointerEvent, bool> handler, IElement element, string attribute,
                        string path, int pointerId, PointerPhase phase, float x, float y)
    {
        // Every pointer currently held BY THIS ELEMENT — what a pinch or a rotate is computed from.
        var mine = new List<CupriPointer>();
        foreach (var (id, pos) in _active)
            if (_captured.TryGetValue(id, out var c) && c.Path == path)
                mine.Add(new CupriPointer(id, pos.X, pos.Y));
        mine.Sort((a, b) => a.Id.CompareTo(b.Id));

        var changed = handler(new MultiPointerEvent(
            pointerId, phase, x, y, mine, element, element.GetAttribute(attribute) ?? "", _model));

        // Re-bind, don't merely mark dirty. A gesture handler's whole job is usually to write to the
        // MODEL — a scale, a rotation, a position — and Bump only advances the version counter, so
        // the new value never reached the DOM: the pinch worked perfectly and nothing moved on
        // screen. The click path has always refreshed here; the pointer path forgot to.
        if (changed) Refresh();
        return Bump(changed);
    }
}
