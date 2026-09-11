using CupriFace.Paint;
using SkiaSharp;

namespace CupriFace.Threading;

/// <summary>
/// Host-facing wrapper over <see cref="ThreadedRenderer"/> that manages the frame handoff for the
/// commit-snapshot split (DESIGN §7.2). The UI thread <see cref="Submit"/>s an immutable
/// <see cref="DisplayList"/> (built by <c>CupriDocument.BuildDisplayList</c>) and, each vsync,
/// <see cref="Present"/>s the latest completed frame onto its window canvas — so rasterisation runs
/// entirely off the UI thread and the UI thread never blocks on it.
///
/// The latest frame lives in one persistent bitmap guarded by a single lock: the render thread copies
/// into it, the UI thread draws from it. No cross-thread image lifetime to race on.
/// </summary>
public sealed class ThreadedPresenter : IDisposable
{
    private readonly ThreadedRenderer _renderer;
    private readonly object _lock = new();
    private SKBitmap? _latest; // render thread writes, UI thread reads — both under _lock
    private bool _hasFrame;

    public ThreadedPresenter() => _renderer = new ThreadedRenderer(OnRendered);

    /// <summary>Frames the render thread has rasterised so far.</summary>
    public long FramesRendered => _renderer.FramesRendered;

    /// <summary>Hand the render thread the latest snapshot to rasterise (non-blocking; latest wins).
    /// <paramref name="scale"/> is the host's logical-to-device factor (monitor DPI × the app's
    /// present scale); 1 means the display list is already in device pixels.</summary>
    public void Submit(DisplayList list, int width, int height, SKColor clear, float scale = 1f) =>
        _renderer.Commit(list, width, height, clear, scale);

    /// <summary>Draw the latest completed frame onto <paramref name="canvas"/> (UI thread). Returns
    /// false if no frame has been rendered yet.</summary>
    public bool Present(SKCanvas canvas)
    {
        lock (_lock)
        {
            if (!_hasFrame || _latest is null) return false;
            canvas.DrawBitmap(_latest, 0, 0);
            return true;
        }
    }

    // Runs on the render thread; copies the just-rasterised image into the persistent latest buffer.
    private void OnRendered(SKImage img)
    {
        lock (_lock)
        {
            if (_latest is null || _latest.Width != img.Width || _latest.Height != img.Height)
            {
                _latest?.Dispose();
                _latest = new SKBitmap(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            }
            img.ReadPixels(_latest.PeekPixels(), 0, 0);
            _hasFrame = true;
        }
    }

    public void Dispose()
    {
        _renderer.Dispose();
        lock (_lock) { _latest?.Dispose(); _latest = null; _hasFrame = false; }
    }
}
