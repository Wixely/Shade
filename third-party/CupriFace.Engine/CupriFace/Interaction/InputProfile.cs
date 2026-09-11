namespace CupriFace.Interaction;

/// <summary>
/// What the app is being driven BY, rather than what OS it happens to be running on. Platform is
/// the wrong axis: a Windows tablet is "desktop" with a coarse pointer and no hover, a laptop with
/// a touchscreen is both at once, and a docked phone with a mouse is a fine pointer on a mobile OS.
/// Keying layout off capability is the distinction that actually holds.
///
/// The engine turns this into body classes — <c>cupri-coarse</c>/<c>cupri-fine</c> and
/// <c>cupri-nohover</c> — and does nothing else with it. Hiding a number field's stepper arrows on
/// a touch device is then a CSS rule, not an engine feature.
/// </summary>
public readonly record struct InputProfile(bool CoarsePointer, bool Hover)
{
    /// <summary>Mouse or trackpad: precise, and it hovers.</summary>
    public static readonly InputProfile Desktop = new(CoarsePointer: false, Hover: true);

    /// <summary>A finger: imprecise, and there is no hover state to read.</summary>
    public static readonly InputProfile Touch = new(CoarsePointer: true, Hover: false);
}
