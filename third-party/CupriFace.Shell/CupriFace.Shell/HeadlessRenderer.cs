using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// CPU-raster render path (DESIGN.md §7.5 — "CPU raster is a fallback"). Runs the
/// same draw contract as <see cref="SkiaWindow"/> against an in-memory Skia surface,
/// with no OS window or GL driver required. Used for headless/CI verification and on
/// machines without hardware OpenGL (e.g. RDP/VM sessions).
/// </summary>
public sealed class HeadlessRenderer
{
    private readonly int _width;
    private readonly int _height;
    private readonly FrameStats _stats = new();

    public FrameStats Stats => _stats;

    public HeadlessRenderer(int width = 1024, int height = 768)
    {
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Renders <paramref name="frames"/> frames through <paramref name="draw"/> onto a
    /// CPU surface and returns a snapshot of the final frame. Frame deltas are simulated
    /// at 60 fps so <see cref="FrameStats"/> reads meaningfully.
    /// </summary>
    public SKImage RenderFrames(int frames, Action<RenderContext> draw)
    {
        var info = new SKImageInfo(_width, _height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        const double simulatedDelta = 1.0 / 60.0;

        for (var i = 0; i < frames; i++)
        {
            _stats.BeginFrame(simulatedDelta);
            draw(new RenderContext(surface.Canvas, _width, _height, _stats));
            surface.Canvas.Flush();
            _stats.EndFrame();
        }

        return surface.Snapshot();
    }
}
