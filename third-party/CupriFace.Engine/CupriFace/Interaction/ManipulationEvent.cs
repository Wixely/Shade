using AngleSharp.Dom;

namespace CupriFace.Interaction;

/// <summary>
/// A drag / pinch / rotate, already worked out. This is the OPTIONAL layer above
/// <c>doc.OnPointer</c>: the raw pointers are still there for anyone who wants them, but the
/// arithmetic everybody would otherwise rewrite — and get subtly wrong — is done once, here.
///
/// The value that is easiest to get wrong is <see cref="FocusX"/>/<see cref="FocusY"/>. A pinch
/// scales about the point BETWEEN the fingers, not about the element's centre; scale about the
/// centre and the content slides out from under the hands that are holding it. (The engine's own
/// first sample made exactly that mistake.)
/// </summary>
public sealed record ManipulationEvent(
    /// <summary>Cumulative scale since the gesture began: 1 = unchanged, 2 = pinched to double.</summary>
    double Scale,

    /// <summary>Cumulative rotation in degrees since the gesture began, clockwise-positive.</summary>
    double Rotation,

    /// <summary>How far the focal point has travelled since the gesture began, in logical px —
    /// one finger dragging, or two fingers moving together.</summary>
    double PanX,
    double PanY,

    /// <summary>Where the gesture is centred RIGHT NOW: the single pointer, or the midpoint of
    /// several. Scale and rotation should be applied about this point.</summary>
    double FocusX,
    double FocusY,

    /// <summary>How many fingers are on this element. Changes mid-gesture without the cumulative
    /// values jumping — adding a second finger to a drag continues it, rather than restarting.</summary>
    int PointerCount,

    PointerPhase Phase,
    IElement Element,
    string Value,
    object? Model);
