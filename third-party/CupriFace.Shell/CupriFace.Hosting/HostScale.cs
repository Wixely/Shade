namespace CupriFace.Hosting;

/// <summary>
/// The three scales a windowed host has to keep apart, and the one number it should actually paint
/// with.
///
/// <para><b>D — device scale.</b> What the OS says a logical pixel is worth on the monitor this
/// window is on: 1.5 at 144 DPI, 2 on a Retina panel, 3 on a phone. The host does not choose it and
/// cannot refuse it; it changes when the window is dragged to another monitor.</para>
///
/// <para><b>P — present scale.</b> What the APPLICATION chose, via <c>PresentInfo.Scale</c> — a
/// design size fitted to the viewport, or a deliberate zoom. Nothing to do with the monitor.</para>
///
/// <para><b>T — effective scale, <c>D * P</c>.</b> The only one that should ever reach a canvas, a
/// surface allocation, a damage rectangle or an accessibility bounding box. Keeping D and P apart
/// right up to this multiply is the whole point of the type: hosts that conflate them either render
/// at logical resolution and get upscaled (soft text), or apply a factor twice (a UI that shrinks
/// every time it is moved to a sharper screen).</para>
///
/// <para><b>Why this is a standalone file with no dependencies.</b> It is shared as SOURCE rather
/// than referenced, so it compiles into any host — desktop today, the Android and web hosts if they
/// adopt it — without dragging the engine, a windowing library or a package edge along with it. That
/// also keeps it out of the browser hosts' ILC input entirely, so it cannot cost a wasm byte it is
/// not being used for. The same pattern as <c>WebCore.props</c> and <c>WebFonts.props</c>.</para>
///
/// <para>Built in two steps because the arithmetic genuinely has two steps, and collapsing them is
/// how the bug gets written: <see cref="ForFramebuffer"/> converts the physical framebuffer into the
/// logical client size the app is then asked to present into, and <see cref="WithPresentScale"/>
/// folds in the answer. Between those two calls the app is told the size of its window in the units
/// it thinks in, which is the thing the desktop host was never doing (#137).</para>
/// </summary>
/// <param name="DeviceScale">D — the OS device-pixel ratio for this window's monitor.</param>
/// <param name="FramebufferWidth">Physical framebuffer width, in device pixels.</param>
/// <param name="FramebufferHeight">Physical framebuffer height, in device pixels.</param>
/// <param name="LogicalClientWidth">Client width in logical pixels — what the app is asked to
/// present into. NOT the same as the viewport it lays out at: an app that answers with
/// <c>PresentInfo.Fixed</c> or <c>Zoom</c> lays out at a different size on purpose.</param>
/// <param name="LogicalClientHeight">Client height in logical pixels.</param>
/// <param name="EffectiveScale">T — <c>D * P</c>. Paint, allocate, damage and publish with this.</param>
public readonly record struct HostScale(
    float DeviceScale,
    int FramebufferWidth,
    int FramebufferHeight,
    float LogicalClientWidth,
    float LogicalClientHeight,
    float EffectiveScale)
{
    /// <summary>A device scale outside this range is a bad reading rather than a display: Windows
    /// tops out at 500%, Android at about 4, and a zero or a NaN would divide the client size to
    /// infinity and lay the document out at nothing. Wider than <c>PresentInfo</c>'s clamp on P
    /// because this end of it is the OS's number, not a design choice.</summary>
    public const float MinDeviceScale = 0.25f, MaxDeviceScale = 8f;

    /// <summary>The identity: no OS scaling, no app scaling, logical == physical. What every host
    /// did implicitly before this type existed, and what a backend that cannot report a scale
    /// should fall back to.</summary>
    public static HostScale Identity(int framebufferWidth, int framebufferHeight) =>
        ForFramebuffer(framebufferWidth, framebufferHeight, 1f);

    /// <summary>
    /// Step one: turn a physical framebuffer plus the monitor's scale into the logical client size
    /// the application should be asked to present into.
    ///
    /// <para><see cref="EffectiveScale"/> is D at this point — correct for a host that never asks
    /// the app anything, and superseded by <see cref="WithPresentScale"/> for one that does.</para>
    /// </summary>
    public static HostScale ForFramebuffer(int framebufferWidth, int framebufferHeight, float deviceScale)
    {
        var d = Sanitize(deviceScale);
        var w = framebufferWidth > 0 ? framebufferWidth : 0;
        var h = framebufferHeight > 0 ? framebufferHeight : 0;
        return new HostScale(d, w, h, w / d, h / d, d);
    }

    /// <summary>
    /// Step two: fold in the scale the application chose, giving <c>T = D * P</c>.
    ///
    /// <para>The client size is deliberately NOT recomputed. It is the size of the window, which the
    /// app's answer does not change — an app that zooms is choosing to lay out at a different
    /// viewport inside the same window, and <c>PresentInfo.LogicalWidth/Height</c> is where that
    /// size lives.</para>
    /// </summary>
    public HostScale WithPresentScale(float presentScale) =>
        this with { EffectiveScale = DeviceScale * Sanitize(presentScale) };

    /// <summary>P, recovered from T and D — the application's own factor, for a host that has to
    /// apply it on its own (pointer coordinates arriving in logical client units, say).</summary>
    public float PresentScale => DeviceScale > 0 ? EffectiveScale / DeviceScale : 1f;

    /// <summary>Physical device pixels → logical client units. What a backend reporting RAW pixel
    /// coordinates (GLFW on Windows) needs; a backend already reporting logical units (GLFW on
    /// macOS, SDL2 without high-DPI) must NOT call this, or it divides by D twice.</summary>
    public float ToLogicalClient(float devicePixels) => devicePixels / DeviceScale;

    /// <summary>Logical client units → document units, by dividing out the application's own scale.
    /// The last hop of a pointer coordinate: a backend normalises to logical client once, and this
    /// takes it the rest of the way to the space the document was laid out in.</summary>
    public float ToDocument(float logicalClient)
    {
        var p = PresentScale;
        return p > 0 ? logicalClient / p : logicalClient;
    }

    /// <summary>True when the OS scale differs from <paramref name="previous"/> — a monitor
    /// transition, which invalidates raster-backed surfaces and any retained frame. Compared with a
    /// tolerance because these arrive as divisions (dpi/96) and 1.25 is not exact in binary.</summary>
    public bool DeviceScaleChangedFrom(HostScale previous) =>
        MathF.Abs(DeviceScale - previous.DeviceScale) > 0.0005f;

    /// <summary>Clamp a reported scale into something a host can divide by. A non-finite or
    /// non-positive reading means the backend could not tell us, which is 1 — never 0, which would
    /// lay the document out at an infinite viewport.</summary>
    public static float Sanitize(float scale) =>
        float.IsFinite(scale) && scale > 0 ? Math.Clamp(scale, MinDeviceScale, MaxDeviceScale) : 1f;
}
