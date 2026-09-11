using CupriFace.Paint;
using CupriFace.Text;
using SkiaSharp;

namespace CupriFace.Threading;

/// <summary>
/// The render-thread side of the commit-snapshot seam (DESIGN.md §7.2). The UI thread
/// produces an immutable <see cref="DisplayList"/> and <see cref="Commit"/>s it; a
/// dedicated background thread rasterises the latest committed snapshot to a CPU surface
/// and hands the result to <c>present</c>. The producer never blocks on rasterisation,
/// and the render thread never touches live tree state — only the immutable snapshot.
///
/// This owns its own <see cref="FontService"/> so its glyph cache is thread-isolated from
/// the layout thread's. A GL backend would instead pin the GL context to this thread.
/// </summary>
public sealed class ThreadedRenderer : IDisposable
{
    private readonly Thread _thread;
    private readonly FontService _fonts = new();
    private readonly SkiaRasterizer _rasterizer;
    private readonly Action<SKImage> _present;

    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private DisplayList? _pending;
    private int _width, _height;
    private float _scale = 1f;
    private SKColor _clear;
    private volatile bool _running = true;

    private long _framesRendered;
    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    public ThreadedRenderer(Action<SKImage> present)
    {
        _present = present;
        _rasterizer = new SkiaRasterizer(_fonts);
        _thread = new Thread(Loop) { IsBackground = true, Name = "CupriFace-Render" };
        _thread.Start();
    }

    /// <summary>Hand the render thread the latest committed snapshot (non-blocking).</summary>
    /// <param name="width">Target surface width in DEVICE pixels.</param>
    /// <param name="height">Target surface height in DEVICE pixels.</param>
    /// <param name="scale">Logical-to-device factor the render thread applies before painting —
    /// the host's effective scale (monitor DPI × the app's present scale). The display list is built
    /// in logical units, so without this the snapshot is rasterised at logical resolution and then
    /// stretched into a physical surface, which is exactly the softness DPI awareness exists to
    /// avoid. Defaults to 1, which is the identity and the previous behaviour.</param>
    public void Commit(DisplayList list, int width, int height, SKColor clear, float scale = 1f)
    {
        lock (_lock)
        {
            _pending = list; _width = width; _height = height; _clear = clear;
            _scale = float.IsFinite(scale) && scale > 0 ? scale : 1f;
        }
        _signal.Set();
    }

    private void Loop()
    {
        while (_running)
        {
            _signal.WaitOne();
            DisplayList? list; int w, h; SKColor clear; float scale;
            lock (_lock)
            {
                list = _pending; _pending = null; w = _width; h = _height; clear = _clear; scale = _scale;
            }
            if (list is null || !_running || w <= 0 || h <= 0) continue;

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(clear);
            // The list is in logical units and the surface is in device pixels; this is the one
            // multiply between them. Vectors and glyphs are re-rasterised at the target size rather
            // than resampled, which is the entire point of doing it here instead of stretching the
            // finished image on present.
            if (scale != 1f) surface.Canvas.Scale(scale);
            _rasterizer.Paint(surface.Canvas, list);
            surface.Canvas.Flush();

            using var image = surface.Snapshot();
            Interlocked.Increment(ref _framesRendered);
            _present(image); // consumed synchronously by the caller
        }
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _thread.Join(1000);
        _signal.Dispose();
        _fonts.Dispose();
    }
}
