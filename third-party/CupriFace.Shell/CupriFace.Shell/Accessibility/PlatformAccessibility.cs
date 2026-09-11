using CupriFace.Accessibility;

namespace CupriFace.Shell.Accessibility;

/// <summary>
/// The host's single accessibility seam: owns whichever platform bridge this OS has (UIA on
/// Windows, AT-SPI on Linux), attaches it lazily, drains its queued AT actions on the UI thread,
/// and publishes a semantics snapshot after a drawn frame.
///
/// It exists so <see cref="DesktopHost"/> says the same three things in all three of its render
/// paths (GL, damage-aware SDL, threaded SDL) instead of repeating per-bridge wiring in each —
/// which is exactly how the second bridge would have tripled a pile of near-identical code.
///
/// Publishing is gated on the document's content version: a playing video repaints 60&#215;/s over a
/// semantics tree that never changed, and rebuilding that snapshot per frame is pure waste.
/// </summary>
internal sealed class PlatformAccessibility(CupriFace.CupriDocument doc, Action requestFrame, string appTitle)
    : IDisposable
{
    private object? _bridge;                    // UiaBridge | AtSpiBridge | NsAccessibilityBridge | null
    private bool _tried;
    private (int Version, float W, float H, float Scale, int X, int Y) _published;

    /// <summary>Per-frame UI-thread work: attach on the first opportunity (Windows and macOS both
    /// need a native window handle, which doesn't exist until the window is up), then run any
    /// actions an AT queued. Returns true when something ran, so the caller can mark the frame
    /// dirty.</summary>
    /// <param name="nativeWindow">The handle this OS's bridge attaches to: the HWND on Windows, the
    /// NSWindow on macOS. Unused on Linux, where AT-SPI needs no window at all.</param>
    public bool Tick(Func<nint?> nativeWindow)
    {
        if (!_tried)
        {
            if (OperatingSystem.IsWindows())
            {
                if (UiaBridge.Enabled && nativeWindow() is { } hwnd)
                {
                    _tried = true;
                    _bridge = UiaBridge.TryAttach(hwnd, doc, requestFrame);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (NsAccessibilityBridge.Enabled && nativeWindow() is { } nsWindow)
                {
                    _tried = true;
                    _bridge = NsAccessibilityBridge.TryAttach(nsWindow, doc, requestFrame, appTitle);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                _tried = true;
                if (AtSpiBridge.Enabled) _bridge = AtSpiBridge.TryAttach(doc, requestFrame, appTitle);
            }
            else
            {
                _tried = true;
            }
        }

        return _bridge switch
        {
            UiaBridge uia when OperatingSystem.IsWindows() => uia.DrainActions(),
            AtSpiBridge atSpi when OperatingSystem.IsLinux() => atSpi.DrainActions(),
            NsAccessibilityBridge nsa when OperatingSystem.IsMacOS() => nsa.DrainActions(),
            _ => false,
        };
    }

    /// <summary>Publish the semantics tree after a drawn frame — but only when the CONTENT (or the
    /// window's geometry) actually changed since the last publish.</summary>
    public void Publish(float logicalWidth, float logicalHeight, float scale, (int X, int Y) clientOrigin)
    {
        if (_bridge is null) return;
        var key = (doc.ContentVersion, logicalWidth, logicalHeight, scale, clientOrigin.X, clientOrigin.Y);
        if (key == _published) return;
        _published = key;

        switch (_bridge)
        {
            case UiaBridge uia when OperatingSystem.IsWindows():
                uia.PublishFrame(logicalWidth, logicalHeight, scale, clientOrigin);
                break;
            case AtSpiBridge atSpi when OperatingSystem.IsLinux():
                atSpi.PublishFrame(logicalWidth, logicalHeight, scale, clientOrigin);
                break;
            case NsAccessibilityBridge nsa when OperatingSystem.IsMacOS():
                nsa.PublishFrame(logicalWidth, logicalHeight, scale, clientOrigin);
                break;
        }
    }

    public void Dispose()
    {
        if (_bridge is AtSpiBridge atSpi && OperatingSystem.IsLinux()) atSpi.Dispose();
        if (_bridge is NsAccessibilityBridge nsa && OperatingSystem.IsMacOS()) nsa.Dispose();
        _bridge = null;
    }
}
