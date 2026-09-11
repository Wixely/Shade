using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CupriFace.Shell;

/// <summary>
/// Where the desktop host gets D on Windows (#137).
///
/// <para><b>Why this is not derived from the windowing backend.</b> GLFW sizes its windows in
/// PHYSICAL pixels on Windows, so framebuffer size and window size are always equal here and their
/// ratio — which is exactly how macOS Retina and Wayland report their scaling for free — is a
/// constant 1 no matter what the monitor is doing. Silk.NET 2.22 binds
/// <c>glfwGetMonitorContentScale</c> but not <c>glfwGetWindowContentScale</c>, and offers no
/// content-scale change callback, so there is no cross-platform route to this number either. That
/// leaves <c>GetDpiForWindow</c>, which is per-window (therefore per-monitor), needs no manifest to
/// READ, and is present from Windows 10 1607.</para>
///
/// <para><b>Two user32 entry points added to a shell that already declares eighteen.</b> This is not
/// a new kind of dependency for CupriFace.Shell — the tray icon, the dark window chrome and the UIA
/// bridge are all native interop already, each guarded exactly like this. The ENGINE remains free of
/// P/Invoke, which is the invariant that matters.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDpi
{
    /// <summary>Per-Monitor-V2. The awareness context a modern desktop app wants: the window is told
    /// its DPI per monitor, non-client area (the title bar) scales itself, and Windows stops
    /// bitmap-virtualising the process.</summary>
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>The one DPI Windows calls 100%. Every scale here is a ratio against it.</summary>
    private const float BaselineDpi = 96f;

    /// <summary>Kill switch, in the shape of <c>CUPRIFACE_UIA</c> / <c>CUPRIFACE_ATSPI</c> /
    /// <c>CUPRIFACE_NSA</c>: on by default, and <c>CUPRIFACE_DPI=0</c> turns the whole feature off —
    /// awareness AND per-monitor tracking — so a machine where scaling misbehaves can be put back on
    /// the old code path without a rebuild.</summary>
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("CUPRIFACE_DPI") is not ("0" or "false" or "FALSE");

    /// <summary>
    /// Declare Per-Monitor-V2 for this process. Must run BEFORE any window exists — awareness is a
    /// process-wide property that windows inherit at creation, so calling it afterwards changes
    /// nothing about the windows already open.
    /// </summary>
    ///
    /// <para><b>Failure is a normal outcome, not an error.</b> If the executable's manifest already
    /// declares an awareness — or the app called this itself before handing control to
    /// <c>DesktopHost.Run</c> — Windows refuses with ERROR_ACCESS_DENIED and the existing choice
    /// stands. That is the required behaviour rather than a problem to work around: an application
    /// that stated its own awareness must win over a library's default (#137). So the result is
    /// advisory, and callers ask the WINDOW for its scale afterwards rather than assuming this
    /// call's outcome.</para>
    ///
    /// <returns>True if this call established the context; false if it was already set by a manifest
    /// or an earlier call, or the API is unavailable.</returns>
    internal static bool TryDeclarePerMonitorV2()
    {
        try
        {
            return SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-1607. The window still opens; it is simply DPI-unaware, which is what it was
            // before this file existed.
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// D for the monitor this window is currently on. Per-WINDOW rather than per-system: the whole
    /// point is that dragging between a 100% and a 150% monitor changes the answer.
    /// </summary>
    /// <returns>The scale (1.5 at 144 DPI), or 1 when the window handle is not usable yet or the API
    /// is unavailable — never 0, which would divide a client size to infinity.</returns>
    internal static float GetScaleForWindow(nint hwnd)
    {
        if (hwnd == 0) return 1f;
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / BaselineDpi : 1f;
        }
        catch (EntryPointNotFoundException)
        {
            return 1f;
        }
        catch (DllNotFoundException)
        {
            return 1f;
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(nint value);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
