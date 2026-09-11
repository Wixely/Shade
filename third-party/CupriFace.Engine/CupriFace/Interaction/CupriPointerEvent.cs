using AngleSharp.Dom;
using CupriFace.Dom;

namespace CupriFace.Interaction;

/// <summary>A pointer event delivered to a click handler.</summary>
public readonly record struct CupriPointerEvent(float X, float Y, RenderNode Target, IElement Element);

/// <summary>An activation of a custom interaction primitive (registered via
/// <c>CupriDocument.OnAction</c>): the element carrying the registered <c>data-*</c> attribute, its
/// value, the bound model, and the pointer position. Fired by click or keyboard activation.</summary>
public readonly record struct CupriActionEvent(RenderNode Node, IElement Element, string Value, object? Model, float X, float Y);

/// <summary>A link (<c>&lt;a href&gt;</c>) was activated (click or Enter) with a non‑anchor href. In‑page
/// <c>#anchor</c> links are handled by the engine itself (it scrolls the target into view) and do NOT raise
/// this. <see cref="External"/> is true only for an absolute URI allowed by
/// <see cref="ExternalLinkPolicy"/> (<c>http:</c>, <c>https:</c>, <c>mailto:</c>, or <c>tel:</c>).
/// Bare paths, custom schemes, and ambiguous protocol-relative links remain available to in-app routing.</summary>
public readonly record struct NavigateEvent(string Href, bool External);
