namespace CupriFace.Hosting;

/// <summary>
/// Keeps a window's LOGICAL size stable across monitor changes.
///
/// <para>This exists because the obvious implementation is wrong in a way that only shows up on a
/// real multi-DPI desk. A window host knows two things — the framebuffer size, and the OS scale —
/// and it is tempting to keep the logical size as <c>framebuffer / scale</c>, recomputed whenever
/// the framebuffer changes. That divides by a STALE scale during the one event that matters: while
/// a window is dragged between monitors the OS runs a modal loop, the host's frame tick never gets
/// a turn, and the framebuffer callback arrives before anything has re-read the DPI. The logical
/// size is then computed with the old divisor and silently corrupted — after which the window never
/// returns to its proper size, because the value it would return to is wrong.</para>
///
/// <para>The fix is to treat the two kinds of resize as opposites, and to say which is which from
/// the scale OBSERVED AT THAT MOMENT rather than from a cached one:</para>
/// <list type="bullet">
///   <item><b>A user resize</b> (scale unchanged) — the logical size is ADOPTED from the new
///   framebuffer. The user decided how big the window is.</item>
///   <item><b>A DPI transition</b> (scale changed) — the logical size is PRESERVED and the physical
///   size is recomputed from it. That is the whole point of Per-Monitor-V2: the window keeps its
///   size on the desk and changes how many pixels it spends on it.</item>
/// </list>
///
/// <para>Reported against the first cut of #137: dragging 100% → 150% resized only on mouse-up, and
/// dragging back to 100% did not resize at all, leaving the window oversized.</para>
/// </summary>
public sealed class WindowScaleTracker(int logicalWidth, int logicalHeight, float deviceScale = 1f)
{
    /// <summary>Below this, two scales are the same number arriving by different routes — 125% is
    /// 120/96, which is not exact in binary and would otherwise flicker as a transition.</summary>
    private const float Epsilon = 0.0005f;

    /// <summary>The OS scale this tracker currently believes it is on.</summary>
    public float DeviceScale { get; private set; } = HostScale.Sanitize(deviceScale);

    /// <summary>The window's size in logical units — the value held stable across monitor changes.</summary>
    public int LogicalWidth { get; private set; } = Math.Max(1, logicalWidth);

    /// <summary>The window's size in logical units.</summary>
    public int LogicalHeight { get; private set; } = Math.Max(1, logicalHeight);

    /// <summary>The physical size the window should have at the current scale, for the logical size
    /// it is holding.</summary>
    public (int Width, int Height) WantedPhysical => Physical(DeviceScale);

    /// <summary>
    /// The OS reports a scale. Call this from every place that could witness a monitor change —
    /// including the window-move and framebuffer-resize callbacks, which are delivered from INSIDE
    /// the modal drag loop, and not only from the frame tick, which is not.
    /// </summary>
    /// <returns>The physical size to resize the window to, or null when the scale had not changed
    /// and nothing needs to happen.</returns>
    public (int Width, int Height)? ObserveScale(float deviceScale)
    {
        var next = HostScale.Sanitize(deviceScale);
        if (MathF.Abs(next - DeviceScale) <= Epsilon) return null;
        DeviceScale = next;
        return Physical(next);   // logical size deliberately untouched — it is what we preserve
    }

    /// <summary>
    /// The framebuffer changed for a reason the host did not cause.
    ///
    /// <para><paramref name="observedDeviceScale"/> must be read from the OS at the moment of the
    /// call, not taken from a cached field — telling a user resize apart from a DPI transition is
    /// the entire job here, and a stale scale makes them indistinguishable.</para>
    /// </summary>
    /// <returns>The physical size to resize the window to when this was a DPI transition; null when
    /// it was an ordinary user resize, which has already been adopted.</returns>
    public (int Width, int Height)? ObserveFramebuffer(int width, int height, float observedDeviceScale)
    {
        if (width <= 0 || height <= 0) return null;

        var next = HostScale.Sanitize(observedDeviceScale);
        if (MathF.Abs(next - DeviceScale) > Epsilon)
        {
            // A DPI transition wearing a resize's clothing. Preserve the logical size.
            DeviceScale = next;
            return Physical(next);
        }

        // An ordinary resize at an unchanged scale: the user is the authority on how big this is.
        LogicalWidth = Math.Max(1, (int)MathF.Round(width / next));
        LogicalHeight = Math.Max(1, (int)MathF.Round(height / next));
        return null;
    }

    private (int Width, int Height) Physical(float scale) => (
        Math.Max(1, (int)MathF.Round(LogicalWidth * scale)),
        Math.Max(1, (int)MathF.Round(LogicalHeight * scale)));
}
