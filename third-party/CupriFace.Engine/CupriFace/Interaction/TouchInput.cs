namespace CupriFace.Interaction;

/// <summary>Tunables for <see cref="TouchInput"/>. Distances are LOGICAL pixels (the same space
/// every Dispatch* call uses), times are seconds on whatever clock the host feeds in.</summary>
public sealed record TouchOptions
{
    /// <summary>Finger travel beyond which a press stops being a tap and becomes a scroll.</summary>
    public float SlopPx { get; init; } = 8f;
    /// <summary>Hold duration that turns a still press into a context menu.</summary>
    public double LongPressSeconds { get; init; } = 0.5;
    /// <summary>Window after a tap's finger-up in which the next tap escalates the click count.</summary>
    public double DoubleTapSeconds { get; init; } = 0.3;
    /// <summary>Radius around the previous tap within which the next one counts as a repeat.</summary>
    public float DoubleTapRadiusPx { get; init; } = 24f;
    /// <summary>Release velocity (px/s) below which lifting the finger just stops the scroll.</summary>
    public float MinFlingPxPerSec { get; init; } = 50f;
}

/// <summary>
/// The engine's touch gesture recognizer: raw finger events in, correct <c>Dispatch*</c> calls
/// out. It exists because of one desktop fact a finger cannot live with — <b>activation fires on
/// pointer-down</b> (<c>DispatchClick</c> IS the down event), so a scroll gesture that began on a
/// button would press the button. This class holds the down back until it knows what the gesture
/// is:
///
///   - a still, short press → the TAP: <c>DispatchClick</c> at finger-up (with double/triple-tap
///     click-count escalation feeding the existing word/line selection),
///   - travel beyond the slop → a SCROLL: per-move <c>DispatchWheel</c> (reusing the engine's
///     chaining and virtual-list re-windowing), with a velocity-tracked fling handed to
///     <see cref="CupriDocument.StartFling"/> at release,
///   - a still, long press → the CONTEXT MENU (<c>DispatchContextMenu</c>), as platforms do,
///   - a dedicated drag surface (slider, scrollbar thumb, reorder handle, split divider, resize
///     grip, table column edge) → forwarded IMMEDIATELY as a mouse-style drag: explicit grips
///     drag from the first touch on every platform.
///
/// Hover never happens: no code path here routes an idle move to the engine's hover update, which
/// is the whole of touch hover-suppression — structural, not a flag.
///
/// Deliberately deterministic: the host supplies every timestamp, and long-press firing is the
/// host calling <see cref="Tick"/> when <see cref="NextDeadline"/> falls due — so every behaviour
/// is testable headlessly with a scripted clock. NOT thread-safe; call it on the document's
/// thread, like every Dispatch*.
/// </summary>
public sealed class TouchInput(CupriDocument document)
{
    private enum Mode { Idle, Pending, Scrolling, Dragging, SwallowUp }

    public TouchOptions Options { get; init; } = new();

    private Mode _mode = Mode.Idle;
    private float _downX, _downY;
    private double _downT;
    private float _prevY, _prevX;
    private string? _scrollPath;
    private string? _scrollPathX;   // the sideways scroller, resolved at the same instant

    // Velocity ring: the last ~100 ms of (time, finger-y) — enough to measure release speed
    // without letting the start of a long drag pollute it.
    private readonly List<(double T, float X, float Y)> _ring = new();

    // Double-tap escalation state (the previous TAP's release).
    private double _lastTapT = double.NegativeInfinity;
    private float _lastTapX, _lastTapY;
    private int _clicks = 1;

    /// <summary>When <see cref="Tick"/> next wants calling (long-press), or null. The host arms a
    /// timer; tests simply call <c>Tick</c> with a scripted time.</summary>
    public double? NextDeadline => _mode == Mode.Pending ? _downT + Options.LongPressSeconds : null;

    /// <summary>Finger down. Returns whether the document changed (repaint).</summary>
    public bool Down(float x, float y, double t)
    {
        // A finger landing mid-fling CATCHES the list: kill the momentum and treat the whole
        // gesture as scrolling — a catch-tap must never click what it happened to land on.
        if (document.FlingActive)
        {
            document.StopFling();
            EnterScroll(x, y, t);
            return false;
        }

        _downX = x; _downY = y; _downT = t;
        _downX0 = x; _downY0 = y;          // the true origin, kept for the axis decision

        if (document.ClassifyPress(x, y) == CupriDocument.PressKind.DragSurface)
        {
            // Dedicated drag affordances drag from the FIRST touch — this is the one case where
            // the mouse semantics (activation on down) are also the touch semantics.
            _mode = Mode.Dragging;
            return document.DispatchClick(x, y);
        }

        // Everything else defers: press feedback now, the decision later.
        _mode = Mode.Pending;
        _clicks = t - _lastTapT <= Options.DoubleTapSeconds
                  && MathF.Abs(x - _lastTapX) <= Options.DoubleTapRadiusPx
                  && MathF.Abs(y - _lastTapY) <= Options.DoubleTapRadiusPx
            ? Math.Min(_clicks + 1, 3) : 1;
        return document.SetPressed(x, y);
    }

    /// <summary>Finger moved. Returns whether the document changed.</summary>
    public bool Move(float x, float y, double t)
    {
        switch (_mode)
        {
            case Mode.Dragging:
                return document.DispatchPointerMove(x, y);

            case Mode.Pending:
                if (MathF.Abs(x - _downX) <= Options.SlopPx && MathF.Abs(y - _downY) <= Options.SlopPx)
                    return false;                       // still a candidate tap — hold
                var cleared = document.ClearPressed();  // it's a scroll: drop :active, never click
                EnterScroll(_downX, _downY, _downT);
                return Scroll(x, y, t) || cleared;      // first delta includes the pre-slop travel

            case Mode.Scrolling:
                return Scroll(x, y, t);

            default:
                return false;
        }
    }

    /// <summary>Finger up. Returns whether the document changed.</summary>
    public bool Up(float x, float y, double t)
    {
        var mode = _mode;
        _mode = Mode.Idle;
        switch (mode)
        {
            case Mode.Dragging:
                return document.DispatchPointerUp(x, y);

            case Mode.Pending:
            {
                // THE TAP — this is where touch activation actually happens. Down coordinates,
                // not up: the finger may have wobbled inside the slop, and the user pressed what
                // they first touched.
                _lastTapT = t; _lastTapX = _downX; _lastTapY = _downY;
                var clicked = document.DispatchClick(_downX, _downY, _clicks);
                var upped = document.DispatchPointerUp(_downX, _downY);
                return clicked || upped;
            }

            case Mode.Scrolling:
            {
                document.ReleaseOverscroll();   // let any stretched band spring back
                Prune(t);
                // Not gated on the VERTICAL target existing: a row that only scrolls sideways
                // has none, and its fling is the one most worth having.
                if (_ring.Count >= 2)
                {
                    // Release velocity over the last ≤100 ms of travel, in ScrollY terms: the
                    // finger moving UP the screen (y shrinking) scrolls content DOWN (positive),
                    // matching DispatchWheel's (prev − cur) convention.
                    var (t0, x0, y0) = _ring[0];
                    var (t1, x1, y1) = _ring[^1];
                    var dt = t1 - t0;
                    if (dt > 0.001)
                    {
                        var v = (y0 - y1) / (float)dt;   // px/s
                        var vx = (x0 - x1) / (float)dt;
                        // One axis flings: the dominant one. A fling that drifted diagonally should
                        // coast the way the user was actually going, not curve.
                        if (MathF.Abs(vx) > MathF.Abs(v))
                        {
                            // Resolved for THIS axis: the nearest sideways-scrolling ancestor is
                            // rarely the same node as the nearest vertical one.
                            if (MathF.Abs(vx) >= Options.MinFlingPxPerSec && _scrollPathX is not null)
                                return document.StartFling(_scrollPathX, vx, horizontal: true);
                        }
                        else if (MathF.Abs(v) >= Options.MinFlingPxPerSec && _scrollPath is not null)
                            return document.StartFling(_scrollPath, v);
                    }
                }
                return false;
            }

            default:
                return false;                            // SwallowUp: the long-press already acted
        }
    }

    /// <summary>Gesture cancelled by the platform (an ancestor stole the pointer, the app lost the
    /// window). Nothing is ever activated by a cancel.</summary>
    public bool Cancel(double t)
    {
        var mode = _mode;
        _mode = Mode.Idle;
        return mode switch
        {
            Mode.Dragging => document.DispatchPointerUp(_downX, _downY),   // end any engine drag
            Mode.Pending => document.ClearPressed(),
            _ => false,
        };
    }

    /// <summary>Fire due deadlines: a still press held past <see cref="TouchOptions.LongPressSeconds"/>
    /// becomes the context menu, and the eventual finger-up is swallowed.</summary>
    public bool Tick(double t)
    {
        if (_mode != Mode.Pending || t < _downT + Options.LongPressSeconds) return false;
        _mode = Mode.SwallowUp;
        var cleared = document.ClearPressed();
        return document.DispatchContextMenu(_downX, _downY) || cleared;
    }

    // ---- internals ----------------------------------------------------------------------------

    private void EnterScroll(float x, float y, double t)
    {
        _mode = Mode.Scrolling;
        // Both axes resolved once, here: the sideways scroller under the finger is rarely the same
        // node as the vertical one, and neither may be re-resolved later without letting the
        // gesture jump scrollers mid-drag.
        _scrollPath = document.ScrollTargetAt(x, y);
        _scrollPathX = document.ScrollTargetAt(x, y, horizontal: true);
        _prevY = _downY = y; _prevX = _downX = x; _downT = t;
        // The origin the axis decision measures from, re-stamped HERE and not only in Down: a
        // finger that catches a fling enters scrolling without going through Down's assignment, so
        // it would otherwise measure its travel from the PREVIOUS gesture's start — instantly
        // clearing the slop and locking whichever axis the stale offset happened to favour. Caught
        // by a sideways drag that followed a fling and was read as vertical.
        _downX0 = x; _downY0 = y;
        _ring.Clear();
        _ring.Add((t, x, y));

        // Which way did the finger COMMIT? A drag is never perfectly straight, so sending both axes
        // meant a sideways drag also crept the page up and down under it. The gesture locks to the
        // axis it started along and stays there; only a genuinely diagonal start (neither axis
        // clearly dominant) moves both, which is what a map or a zoomed image wants.
        _axis = Axis.Both;
        _axisDecided = false;
        DecideAxis(x, y);
    }

    /// <summary>Claim an axis once the finger has travelled far enough to have meant it. Deferred
    /// rather than decided at slop, because a fling-CATCH enters scrolling with no movement at all
    /// and would otherwise be stuck reading "diagonal" for the whole gesture.</summary>
    private void DecideAxis(float x, float y)
    {
        if (_axisDecided) return;
        var dx = MathF.Abs(x - _downX0);
        var dy = MathF.Abs(y - _downY0);
        if (MathF.Max(dx, dy) < Options.SlopPx) return;

        _axis = dx > dy * AxisBias ? Axis.Horizontal
              : dy > dx * AxisBias ? Axis.Vertical
              : Axis.Both;              // a genuine diagonal moves both, which a map wants
        _axisDecided = true;
    }

    private enum Axis { Both, Horizontal, Vertical }
    private Axis _axis;
    private bool _axisDecided;
    private float _downX0, _downY0;      // where the finger first touched, before any slop
    private const float AxisBias = 1.6f; // how decisively one axis must lead to claim the gesture

    private bool Scroll(float x, float y, double t)
    {
        var delta = _prevY - y;                          // finger up (y shrinks) → scroll down (+)
        var deltaX = _prevX - x;                         // finger left (x shrinks) → scroll right (+)
        _prevY = y; _prevX = x;
        DecideAxis(x, y);
        if (_axis == Axis.Horizontal) delta = 0;         // committed sideways: don't creep vertically
        if (_axis == Axis.Vertical) deltaX = 0;
        _ring.Add((t, x, y));
        Prune(t);
        // Scroll the scrollers CAPTURED at gesture start, not whatever is under the finger now.
        // The finger holds one point while the content slides beneath it, so re-resolving per move
        // lets an inner scroller that drifts under that point hijack a drag begun on the page.
        // Both axes go in one call so a diagonal drag moves a sideways row and the page under it
        // together, each taking the part it can use.
        if (delta == 0 && deltaX == 0) return false;

        var scrolled = document.ScrollCaptured(_scrollPath, _scrollPathX, delta, deltaX);
        // Nothing moved and the finger is still pulling: the scroller is at its edge. Stretch it,
        // so arriving at the end feels like arriving rather than like the app going dead. Whichever
        // axis the finger is actually working gets the band — sideways ends deserve the same signal
        // the bottom of a page gets.
        if (!scrolled)
        {
            if (delta != 0 && _scrollPath is not null)
                return document.Overscroll(_scrollPath, delta);
            if (deltaX != 0 && _scrollPathX is not null)
                return document.Overscroll(_scrollPathX, deltaX, horizontal: true);
        }
        return scrolled;
    }

    private void Prune(double t)
    {
        while (_ring.Count > 0 && t - _ring[0].T > 0.1) _ring.RemoveAt(0);
    }
}
