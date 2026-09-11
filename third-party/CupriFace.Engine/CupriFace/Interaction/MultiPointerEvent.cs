using AngleSharp.Dom;

namespace CupriFace.Interaction;

/// <summary>Where one pointer is, right now.</summary>
public readonly record struct CupriPointer(int Id, float X, float Y);

/// <summary>What phase of its life a pointer is in.</summary>
public enum PointerPhase { Down, Move, Up, Cancel }

/// <summary>
/// One raw pointer event delivered to an author's element (see <c>CupriDocument.OnPointer</c>).
/// Distinct from <see cref="CupriPointerEvent"/>, which is the engine's own single-pointer notion.
///
/// <see cref="Pointers"/> carries EVERY pointer this element currently holds — including the one
/// this event is about — which is what a pinch, a rotate or a two-finger drag is computed from.
/// The engine deliberately does not compute those: what a second finger means is the author's
/// decision, and guessing would be worse than not guessing.
/// </summary>
public sealed record MultiPointerEvent(
    int Id,
    PointerPhase Phase,
    float X,
    float Y,
    IReadOnlyList<CupriPointer> Pointers,
    IElement Element,
    string Value,
    object? Model);
