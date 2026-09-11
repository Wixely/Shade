using System.Diagnostics;
using CupriFace.Hosting;
using CupriFace.Interaction;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// M0 shell (DESIGN.md Layer 0 + Layer 4 bootstrap). Owns an OS window (Silk.NET),
/// an OpenGL context, and a Skia GPU surface bound to the default framebuffer.
/// Raises <see cref="Render"/> once per vsync'd frame with a ready-to-draw
/// <see cref="RenderContext"/>.
///
/// SCOPE NOTE: this is the single-threaded foundation. The render-thread /
/// commit-snapshot split (DESIGN.md §7.2) is the next increment of M0 and layers
/// on top of this without changing the public draw contract.
/// </summary>
public sealed class SkiaWindow : IDisposable
{
    private readonly WindowOptions _options;
    private readonly FrameStats _stats = new();
    private readonly bool _darkWindowChrome;
    private readonly SKColor _windowChromeColor;

    private IWindow? _window;
    private IInputContext? _input;
    private GRGlInterface? _glInterface;
    private GRContext? _grContext;
    private GRBackendRenderTarget? _renderTarget;
    private SKSurface? _surface;
    private Vector2D<int> _fbSize;

    /// <summary>Raised each frame after the surface is ready. Draw here.</summary>
    public event Action<RenderContext>? Render;

    /// <summary>Raised once per loop iteration, drawn or skipped, on the UI thread — the hook for
    /// host work that must run there every frame (e.g. draining the UIA action queue). Fires
    /// before the render-or-skip decision, so work done here can dirty this same frame.</summary>
    public event Action? Tick;

    /// <summary>The Win32 window handle once the window exists; null before <see cref="Run"/> and
    /// on every other OS. What the UIA bridge attaches to.</summary>
    public nint? Win32Hwnd => _window?.Native?.Win32?.Hwnd;

    /// <summary>The NSWindow once the window exists; null before <see cref="Run"/> and on every
    /// other OS. What the NSAccessibility bridge subclasses the content view of.</summary>
    public nint? CocoaWindow => _window?.Native?.Cocoa;

    /// <summary>Screen position of the client area's top-left (GLFW reports the content area, which
    /// is exactly the origin pointer coordinates are relative to).</summary>
    public (int X, int Y) ScreenPosition => _window is { } w ? (w.Position.X, w.Position.Y) : (0, 0);

    /// <summary>Nudge the window by a delta, for a frameless window being dragged by an element that
    /// stands in for its missing title bar. A delta rather than a destination because that is what the
    /// engine can report — it knows how far the pointer travelled, not where the window sits.</summary>
    public void MoveBy(int dx, int dy)
    {
        if (_window is not { } w) return;
        w.Position = new Silk.NET.Maths.Vector2D<int>(w.Position.X + dx, w.Position.Y + dy);
    }

    /// <summary>True while the window is OS-fullscreen (see <see cref="SetFullscreen"/>).</summary>
    public bool IsFullscreen => _window?.WindowState == WindowState.Fullscreen;

    // The state to restore on exit — a maximized window must come back maximized, not Normal.
    private WindowState _beforeFullscreen = WindowState.Normal;

    /// <summary>Enter/leave fullscreen (the host maps <c>WindowCommandRequested</c> and the
    /// Escape-to-exit convention here). Resize events flow as normal, so the app reflows.</summary>
    public void SetFullscreen(bool on)
    {
        if (_window is null || IsFullscreen == on) return;
        if (on)
        {
            _beforeFullscreen = _window.WindowState;
            _window.WindowState = WindowState.Fullscreen;
        }
        else
        {
            _window.WindowState = _beforeFullscreen == WindowState.Fullscreen ? WindowState.Normal : _beforeFullscreen;
        }
        _forceRender = true;
    }

    /// <summary>Change the native always-on-top state while the window is running.</summary>
    public void SetTopMost(bool on)
    {
        if (_window is not null)
        {
            _window.TopMost = on;
        }
    }

    // ---- device scale (#137) -------------------------------------------------------------------

    private readonly bool _dpiAware;
    private readonly bool _trackMonitorDpi;
    private float _deviceScale = 1f;
    private Vector2D<int> _logicalSize;                 // seeds the tracker once the window exists
    private WindowScaleTracker? _scale;                 // owns the logical size after that
    private bool _resizingForDpi;                       // re-entrancy: our own resize is not a user's

    /// <summary>Whether the DPI feature is switched on at all — the process-wide kill switch, read
    /// once here so the non-Windows paths do not have to know about the Windows helper.</summary>
    private static bool WindowsDpiEnabled => !OperatingSystem.IsWindows() || WindowsDpi.Enabled;

    /// <summary>
    /// D: what the OS says a logical pixel is worth on the monitor this window is on. 1 until the
    /// window exists, and 1 for the whole life of a window created with <c>dpiAware: false</c>.
    /// </summary>
    public float DeviceScale => _deviceScale;

    /// <summary>Raised when the window lands on a monitor with a different scale, with the new D.
    /// The host rebuilds anything sized in device pixels and forces a clean frame — a raster surface
    /// allocated for the old scale is the wrong number of pixels for the new one.</summary>
    public event Action<float>? DeviceScaleChanged;

    /// <summary>
    /// Read D from whichever source this platform actually has.
    ///
    /// <para>Windows needs asking directly: GLFW sizes its windows in physical pixels there, so the
    /// framebuffer-to-window ratio every other platform reports its scaling through is a constant 1
    /// no matter what the monitor is set to. Everywhere else that ratio IS the scale — 2 on a Retina
    /// panel, 1 on X11 — and costs no P/Invoke to read.</para>
    /// </summary>
    private float ReadDeviceScale()
    {
        if (!_dpiAware) return 1f;
        if (OperatingSystem.IsWindows())
            return Win32Hwnd is { } hwnd ? HostScale.Sanitize(WindowsDpi.GetScaleForWindow(hwnd)) : _deviceScale;
        if (_window is { } w && w.Size.X > 0 && w.FramebufferSize.X > 0)
            return HostScale.Sanitize((float)w.FramebufferSize.X / w.Size.X);
        return 1f;
    }

    /// <summary>
    /// GLFW's pointer coordinates → logical client units, done ONCE, here, because the conversion is
    /// a property of the backend rather than of the app.
    ///
    /// <para>GLFW reports the cursor in window units, which are physical pixels on Windows and
    /// points on macOS. The ratio that normalises both is the logical client size over the window
    /// size: on Windows that is 1/D (the host must divide), on Retina it is exactly 1 (the host must
    /// NOT — dividing by D again is the double-divide that puts a click two-thirds of the way to
    /// where the user aimed).</para>
    /// </summary>
    private (float X, float Y) ToLogicalClient(float x, float y)
    {
        if (_window is not { } w || w.Size.X <= 0 || w.Size.Y <= 0 || _fbSize.X <= 0) return (x, y);
        return (x * (_fbSize.X / _deviceScale) / w.Size.X,
                y * (_fbSize.Y / _deviceScale) / w.Size.Y);
    }

    /// <summary>
    /// Follow the window onto another monitor: re-read D and, when it moved, resize the window so it
    /// keeps the same LOGICAL size — which is what Per-Monitor-V2 is for. The window stays the same
    /// physical size on the desk and simply gains or loses pixels; without this it would keep its
    /// pixel count and visibly shrink on the sharper screen.
    /// </summary>
    private void PollDeviceScale()
    {
        if (!_dpiAware || !_trackMonitorDpi || _window is null || _scale is null || _resizingForDpi) return;
        ApplyDpiResize(_scale.ObserveScale(ReadDeviceScale()));
    }

    /// <summary>
    /// Act on a transition the tracker reported: resize to keep the logical size, drop the surfaces
    /// and tell the host.
    ///
    /// <para>Guarded against re-entering itself, because setting <c>Size</c> delivers the framebuffer
    /// callback SYNCHRONOUSLY — and that callback is one of the places this is called from.</para>
    /// </summary>
    private void ApplyDpiResize((int Width, int Height)? wanted)
    {
        if (_scale is null) return;
        _deviceScale = _scale.DeviceScale;
        if (wanted is not { } size || _window is null) return;

        DpiTrace.Significant(
            $"GL scale -> {_deviceScale:0.###}, resize to {size.Width}x{size.Height} " +
            $"(logical {_scale.LogicalWidth}x{_scale.LogicalHeight})");
        _resizingForDpi = true;
        try
        {
            var v = new Vector2D<int>(size.Width, size.Height);
            if (_window.Size != v) _window.Size = v;
            _fbSize = _window.FramebufferSize;
        }
        catch (Exception ex)
        {
            // Resizing from inside a resize callback is refused on some platforms. Survivable — the
            // window is merely the wrong size until the next event — but never silent.
            if (!_dpiResizeFailureReported)
            {
                _dpiResizeFailureReported = true;
                Console.Error.WriteLine(
                    $"[CupriFace] DPI resize refused ({ex.GetType().Name}: {ex.Message}); " +
                    "the window keeps its pixel size across this monitor change.");
            }
        }
        finally { _resizingForDpi = false; }

        // The surface is sized in device pixels, so it is now the wrong shape whether or not the
        // framebuffer callback fired; drop it and force a repaint rather than trusting the resize.
        _surface?.Dispose(); _surface = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _forceRender = true;
        DeviceScaleChanged?.Invoke(_deviceScale);
    }

    private bool _dpiResizeFailureReported;

    /// <summary>Raised on left-button press with client-area coordinates and the click count
    /// (1/2/3 = single/double/triple — for word/line text selection).</summary>
    public event Action<float, float, int>? PointerDown;
    public event Action<float, float>? RightPointerDown;     // right-click → context menu
    public event Action<float, float>? PointerMove;
    public event Action<float, float>? PointerUp;
    public event Action<float, float, float, KeyMods>? PointerWheel; // x, y, deltaY (notches), mods — Ctrl+wheel is zoom
    public event Action<string>? TextEntered;
    public event Action<EditKey, KeyMods>? EditKeyPressed;  // key + Shift/Ctrl modifiers
    public event Action<char, KeyMods>? Shortcut;           // Ctrl/Cmd + letter (a/c/x/v …) or =/-/0 (zoom)

    /// <summary>OS clipboard text, for copy/cut/paste (Silk keyboard, no P/Invoke).</summary>
    public string? ClipboardText
    {
        get => _input?.Keyboards is { Count: > 0 } ks ? ks[0].ClipboardText : null;
        set { if (value is not null && _input is not null) foreach (var kb in _input.Keyboards) kb.ClipboardText = value; }
    }

    // Click-count tracking (Silk MouseDown carries no count, unlike SDL): rapid clicks near
    // the same point escalate 1→2→3 for word/line selection.
    private readonly Stopwatch _clickClock = Stopwatch.StartNew();
    private double _lastClickMs; private float _lastClickX, _lastClickY; private int _clickCount;

    /// <summary>
    /// Optional per-frame predicate to request window close (used by the headless
    /// smoke test so a run terminates instead of blocking on the render loop).
    /// </summary>
    public Func<FrameStats, bool>? ShouldClose { get; set; }

    /// <summary>Render-on-demand gate: consulted each frame; false skips drawing AND swapping, so a
    /// static UI costs ~nothing (the front buffer stays on screen). Resize/state/focus changes force
    /// the next frame regardless. Null = render every frame (the old behaviour).</summary>
    public Func<bool>? ShouldRender { get; set; }
    private bool _forceRender = true; // first frame, resize, restore, focus — must repaint

    // Pending window icon (RGBA8888) — set before Run(), applied in OnLoad when the window exists.
    private (byte[] Rgba, int W, int H)? _pendingIcon;

    /// <summary>Set the OS window/taskbar icon from raw RGBA8888 pixels (any square size; the
    /// platform scales). Call before <see cref="Run"/>.</summary>
    public void SetIcon(byte[] rgba, int width, int height) => _pendingIcon = (rgba, width, height);

    public FrameStats Stats => _stats;

    public SkiaWindow(string title = "CupriFace", int width = 1024, int height = 768,
        bool transparent = false, bool frameless = false, bool topMost = false,
        bool darkWindowChrome = false, SKColor? windowChromeColor = null,
        bool dpiAware = true, bool trackMonitorDpi = true)
    {
        _darkWindowChrome = darkWindowChrome;
        _windowChromeColor = windowChromeColor ?? new SKColor(0x20, 0x20, 0x20);
        _dpiAware = dpiAware && WindowsDpiEnabled;
        _trackMonitorDpi = trackMonitorDpi;
        _logicalSize = new Vector2D<int>(width, height);
        _options = WindowOptions.Default with
        {
            Title = title,
            Size = new Vector2D<int>(width, height),
            VSync = true,               // pace to the display (§7.1 target)
            API = GraphicsAPI.Default,  // OpenGL, double-buffered
            // Cross-platform (GLFW) window traits — no OS-specific code. Transparency needs a
            // compositing window manager (universal on Win8+/macOS/modern Linux); it degrades to
            // an opaque black background where none is present — the host environment's concern.
            TransparentFramebuffer = transparent,
            WindowBorder = frameless ? WindowBorder.Hidden : WindowBorder.Resizable,
            TopMost = topMost,
            // Render-on-demand: we swap manually, ONLY on frames we actually drew — a skipped frame
            // must not flip an undrawn back buffer onto the screen.
            ShouldSwapAutomatically = false,
        };
    }

    public void Run()
    {
        _window = Window.Create(_options);
        _window.Load += OnLoad;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Render += OnRender;
        _window.Closing += DisposeGpu;
        _window.Run();
    }

    /// <summary>Attempt GL bring-up end to end — window, context, usable GL, Skia interface,
    /// GRContext — on an invisible throwaway window, then tear it all down. Run this in a
    /// DISPOSABLE CHILD PROCESS: on a machine with no OpenGL at all (the paravirtual GPU of
    /// virtualised Macs) glfwCreateWindow fails without setting a GLFW error, Silk.NET then
    /// applies the default window position to the NULL handle, and release-build GLFW (asserts
    /// compiled out) dies with a native SIGSEGV inside glfwSetWindowPos. That cannot be caught —
    /// but it can be CONTAINED in a process whose whole job is to die so the real one doesn't.
    /// A managed failure (no context, unusable GL) throws instead; the child maps both to its
    /// exit code. See DesktopHost.GlProbeSurvives.</summary>
    public static void Probe()
    {
        var options = WindowOptions.Default with
        {
            Title = "cupriface-gl-probe",
            Size = new Vector2D<int>(64, 64),
            IsVisible = false,
            API = GraphicsAPI.Default,
            ShouldSwapAutomatically = false,
        };
        using var window = Window.Create(options);
        window.Load += () =>
        {
            var ctx = window.GLContext
                ?? throw new InvalidOperationException("Probe window was created without a GL context.");
            VerifyGlIsUsable(ctx);
            using var iface = GRGlInterface.Create(name =>
                name.StartsWith("gl", StringComparison.Ordinal) && ctx.TryGetProcAddress(name, out var addr)
                    ? addr : IntPtr.Zero)
                ?? throw new InvalidOperationException("Failed to assemble Skia GL interface.");
            using var gr = GRContext.CreateGl(iface)
                ?? throw new InvalidOperationException("Failed to create Skia GL context.");
            GlTrace("probe: GL bring-up OK");
            window.Close();
        };
        window.Run();
    }

    // CUPRIFACE_GL_DEBUG=1 traces GL bring-up to stderr: the context's version/vendor/renderer,
    // then every proc-address Skia requests. When a broken GL stack kills the process natively,
    // the last trace line names the exact call that did it — evidence obtainable no other way,
    // because the crash is a SIGSEGV inside Skia, past any managed catch.
    private static readonly bool GlDebug =
        Environment.GetEnvironmentVariable("CUPRIFACE_GL_DEBUG") is "1" or "true" or "TRUE";

    private static void GlTrace(string message)
    {
        if (!GlDebug) return;
        Console.Error.WriteLine($"[gl] {message}");
        Console.Error.Flush(); // the process may die on the very next native call
    }

    /// <summary>Throws unless the current context can actually answer <c>glGetString(GL_VERSION)</c>.
    /// Cheap, and it is the exact call whose null result crashes Skia's interface assembly.</summary>
    private static unsafe void VerifyGlIsUsable(Silk.NET.Core.Contexts.IGLContext ctx)
    {
        // If the loader cannot even produce glGetString there is certainly no GL here, and calling
        // through a null address would be its own segfault.
        if (!ctx.TryGetProcAddress("glGetString", out var addr) || addr == IntPtr.Zero)
            throw new InvalidOperationException("No usable OpenGL: glGetString could not be resolved.");

        // Call it through Silk rather than a hand-rolled function pointer: it already knows each
        // platform's calling convention, and this keeps the shell free of bespoke native interop.
        using var gl = Silk.NET.OpenGL.GL.GetApi(ctx);
        if (gl.GetString(Silk.NET.OpenGL.StringName.Version) is null)
            throw new InvalidOperationException(
                "No usable OpenGL: glGetString(GL_VERSION) returned null (GPU-less or virtual display?).");

        if (GlDebug)
        {
            GlTrace($"version : {gl.GetStringS(Silk.NET.OpenGL.StringName.Version)}");
            GlTrace($"vendor  : {gl.GetStringS(Silk.NET.OpenGL.StringName.Vendor)}");
            GlTrace($"renderer: {gl.GetStringS(Silk.NET.OpenGL.StringName.Renderer)}");
        }
    }

    private void OnLoad()
    {
        if (_darkWindowChrome && Win32Hwnd is { } hwnd)
        {
            WindowChrome.TryEnableDarkMode(hwnd, _windowChromeColor);
        }

        var ctx = _window!.GLContext
            ?? throw new InvalidOperationException("Window was created without a GL context.");

        // A context object is not the same as usable GL. On a GPU-less or virtual X server
        // (headless CI, many remote/VM sessions) GLFW hands back a context whose entry points
        // resolve but do nothing: glGetString(GL_VERSION) returns NULL. Skia's interface assembly
        // then parses that null pointer and takes the whole process down with SIGSEGV — captured
        // under gdb in CI, crashing inside gr_glinterface_assemble_interface.
        //
        // A native crash cannot be caught, so the fallback in DesktopHost never got a chance. Ask
        // the context the same question Skia is about to ask, and turn a silent kill into an
        // ordinary exception that the SDL software path already handles.
        VerifyGlIsUsable(ctx);

        // Feed Skia the window's GL loader so it resolves the same context. With CUPRIFACE_GL_DEBUG
        // every lookup is traced BEFORE it resolves — if assembling the interface dies natively, the
        // final trace line is the killer.
        GlTrace("assembling Skia GL interface…");
        _glInterface = GRGlInterface.Create(name =>
        {
            GlTrace($"proc? {name}");        // logged BEFORE the lookup: if the lookup itself dies, this line names it

            // Answer ONLY for OpenGL ("gl*") names. Skia also probes for EGL entry points
            // (eglQueryString, eglGetCurrentDisplay) through this same loader, and on X11 the
            // loader is glXGetProcAddressARB — which is SPECIFIED to fabricate a dispatch stub
            // for any name it does not recognise. It returned non-null garbage for the egl*
            // probes, Skia concluded EGL was present, called the stub, and the process died in
            // an uninitialised dispatch slot (the headless-Linux SIGSEGV; the runner's own GL
            // trace named these two probes as the killer). A null here is fully handled: Skia
            // just skips EGL-specific extension detection, which a GLX/WGL context hasn't got.
            if (!name.StartsWith("gl", StringComparison.Ordinal))
            {
                GlTrace("   -> null (non-GL name; loaders lie about these)");
                return IntPtr.Zero;
            }

            var found = ctx.TryGetProcAddress(name, out var addr);
            GlTrace($"   -> {(found ? $"0x{addr:x}" : "null")}");
            return found ? addr : IntPtr.Zero;
        }) ?? throw new InvalidOperationException("Failed to assemble Skia GL interface.");
        GlTrace("interface assembled; creating GRContext…");

        _grContext = GRContext.CreateGl(_glInterface)
            ?? throw new InvalidOperationException("Failed to create Skia GL context.");
        GlTrace("GRContext created.");

        _fbSize = _window.FramebufferSize;

        if (_pendingIcon is { } icon)
        {
            var raw = new Silk.NET.Core.RawImage(icon.W, icon.H, icon.Rgba);
            _window.SetWindowIcon(ref raw);
        }

        // Being restored/refocused can invalidate what's on screen — repaint on the next frame.
        _window.StateChanged += _ => _forceRender = true;
        _window.FocusChanged += f => { _forceRender = true; KeyDiag.Log(f ? "gl focus-gained" : "gl focus-lost"); };

        _input = _window.CreateInput();
        foreach (var mouse in _input.Mice)
        {
            // Every one of these normalises to logical client units FIRST (see ToLogicalClient):
            // the host downstream only ever divides out the application's own present scale, and
            // never learns which backend it is talking to.
            mouse.MouseDown += (m, btn) =>
            {
                var (x, y) = ToLogicalClient(m.Position.X, m.Position.Y);
                if (btn == MouseButton.Left) PointerDown?.Invoke(x, y, NextClickCount(x, y));
                else if (btn == MouseButton.Right) RightPointerDown?.Invoke(x, y);
            };
            mouse.MouseUp += (m, btn) =>
            {
                if (btn != MouseButton.Left) return;
                var (x, y) = ToLogicalClient(m.Position.X, m.Position.Y);
                PointerUp?.Invoke(x, y);
            };
            mouse.MouseMove += (m, pos) =>
            {
                var (x, y) = ToLogicalClient(pos.X, pos.Y);
                PointerMove?.Invoke(x, y);
            };
            mouse.Scroll += (m, wheel) =>
            {
                var (x, y) = ToLogicalClient(m.Position.X, m.Position.Y);
                PointerWheel?.Invoke(x, y, wheel.Y, _input.Keyboards.Any(Ctrl) ? KeyMods.Ctrl : KeyMods.None);
            };
        }
        foreach (var kb in _input.Keyboards)
        {
            // Skip control chars (Ctrl+letter): those are shortcuts, handled in KeyDown below.
            kb.KeyChar += (k, ch) => { if (!Ctrl(k) && !char.IsControl(ch)) TextEntered?.Invoke(ch.ToString()); };
            kb.KeyDown += (k, key, _) =>
            {
                KeyDiag.Log($"gl keydown key={key} ctrl={Ctrl(k)}");
                var shift = k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight);
                var mods = (shift ? KeyMods.Shift : 0) | (Ctrl(k) ? KeyMods.Ctrl : 0);
                // Any Ctrl/Cmd + letter is forwarded as a chord (see the SDL window for why): the host
                // consumes the clipboard/undo ones, the rest reach the app's own OnShortcut bindings.
                if (Ctrl(k) && key is >= Key.A and <= Key.Z)
                {
                    Shortcut?.Invoke((char)('a' + (key - Key.A)), mods);
                    return;
                }
                // Ctrl/Cmd + =/-/0 is page zoom, browser-style; keypad +/-/0 carry the same intent.
                if (Ctrl(k))
                {
                    var zoomCh = key switch
                    {
                        Key.Equal or Key.KeypadAdd => '=',
                        Key.Minus or Key.KeypadSubtract => '-',
                        Key.Number0 or Key.Keypad0 => '0',
                        _ => '\0',
                    };
                    if (zoomCh != '\0') { Shortcut?.Invoke(zoomCh, mods); return; }
                }
                var ek = key switch
                {
                    Key.Backspace => EditKey.Backspace,
                    Key.Delete => EditKey.Delete,
                    Key.Left => EditKey.Left,
                    Key.Right => EditKey.Right,
                    Key.Home => EditKey.Home,
                    Key.End => EditKey.End,
                    Key.Enter or Key.KeypadEnter => EditKey.Enter,
                    Key.Up => EditKey.Up,
                    Key.Down => EditKey.Down,
                    Key.Tab => shift ? EditKey.ShiftTab : EditKey.Tab,
                    Key.Escape => EditKey.Escape,
                    _ => EditKey.None,
                };
                if (ek != EditKey.None) EditKeyPressed?.Invoke(ek, mods);
            };
        }

        // D, and the window's real size — LAST in OnLoad on purpose. The window was created at the
        // app's size in LOGICAL units, which GLFW took as pixels, so on a scaled monitor it is
        // currently too small; correcting it here makes it the physical size that logical size
        // deserves. Setting Size fires the framebuffer-resize callback, which repaints synchronously
        // (see OnFramebufferResize) — so this must come after everything a frame might touch is
        // built, which is why it is not up beside the first FramebufferSize read.
        _deviceScale = ReadDeviceScale();
        _scale = new WindowScaleTracker(_logicalSize.X, _logicalSize.Y, _deviceScale);
        if (_deviceScale != 1f)
        {
            _resizingForDpi = true;
            try
            {
                var wanted = _scale.WantedPhysical;
                _window.Size = new Vector2D<int>(wanted.Width, wanted.Height);
                _fbSize = _window.FramebufferSize;
            }
            finally { _resizingForDpi = false; }
            // Re-read: on macOS the ratio that produces D only settles once the window has its real
            // size, and on Windows growing the window can cross a monitor boundary.
            ApplyDpiResize(_scale.ObserveScale(ReadDeviceScale()));
        }

        // A monitor change is a MOVE before it is anything else, and GLFW delivers this throughout
        // the drag — including from inside the modal loop that starves the render tick. Without it
        // the window only caught up on mouse-release, which is the "it resizes when I let go"
        // half of the report.
        _window.Move += pos =>
        {
            DpiTrace.Callback("move", $"pos={pos.X},{pos.Y} read={ReadDeviceScale():0.###} cached={_deviceScale:0.###}");
            PollDeviceScale();
        };
    }

    private CupriFace.Style.CursorType _lastCursor = (CupriFace.Style.CursorType)(-1);

    /// <summary>Show the standard cursor matching the engine's <see cref="CupriFace.Style.CursorType"/>
    /// (from <c>CupriDocument.CursorAt</c>). Synonyms fold onto the nearest GLFW standard cursor
    /// (grab → hand; wait/progress/help have no distinct shape → the default arrow).</summary>
    public void SetCursor(CupriFace.Style.CursorType c)
    {
        if (c == _lastCursor || _input?.Mice is not { Count: > 0 } mice) return;
        _lastCursor = c;
        var shape = c switch
        {
            CupriFace.Style.CursorType.Pointer => StandardCursor.Hand,
            // Neither GLFW nor SDL has an open/closed hand, so grab used to fall in with Pointer —
            // which made a drag handle look exactly like a hyperlink. The four-way move arrow is the
            // closest thing either platform has to "this can be dragged", and it is at least DIFFERENT
            // from a link, which is the part that was actually missing.
            CupriFace.Style.CursorType.Grab or CupriFace.Style.CursorType.Grabbing
                or CupriFace.Style.CursorType.Move => StandardCursor.ResizeAll,
            CupriFace.Style.CursorType.Text => StandardCursor.IBeam,
            // Present in GLFW all along and simply never mapped here, while the SDL window has mapped
            // both since it was written — so a busy app showed an hourglass on one desktop path and an
            // arrow on the other. The two tables are now checked against each other by a test.

            CupriFace.Style.CursorType.Wait => StandardCursor.Wait,
            CupriFace.Style.CursorType.Progress => StandardCursor.WaitArrow,
            CupriFace.Style.CursorType.Crosshair => StandardCursor.Crosshair,
            CupriFace.Style.CursorType.NotAllowed => StandardCursor.NotAllowed,
            CupriFace.Style.CursorType.EwResize => StandardCursor.HResize,
            CupriFace.Style.CursorType.NsResize => StandardCursor.VResize,
            CupriFace.Style.CursorType.NwseResize => StandardCursor.NwseResize,
            CupriFace.Style.CursorType.NeswResize => StandardCursor.NeswResize,
            // Help (the arrow-and-question-mark) has no GLFW or SDL standard cursor, so it falls
            // through here on purpose rather than by omission — the one CursorType neither desktop
            // path can honour.
            _ => StandardCursor.Default,
        };
        foreach (var m in mice) { m.Cursor.Type = Silk.NET.Input.CursorType.Standard; m.Cursor.StandardCursor = shape; }
    }

    private static bool Ctrl(IKeyboard k) =>
        k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight) ||
        k.IsKeyPressed(Key.SuperLeft) || k.IsKeyPressed(Key.SuperRight); // Cmd on macOS

    private int NextClickCount(float x, float y)
    {
        var now = _clickClock.Elapsed.TotalMilliseconds;
        _clickCount = (now - _lastClickMs <= 400 && Math.Abs(x - _lastClickX) < 5 && Math.Abs(y - _lastClickY) < 5)
            ? _clickCount + 1 : 1;
        _lastClickMs = now; _lastClickX = x; _lastClickY = y;
        return _clickCount;
    }

    /// <summary>Frames drawn from inside a resize callback rather than the normal loop — the ones
    /// that make a drag-resize stream instead of snapping on release. Zero of these after a drag
    /// means the mechanism below is not working, whatever the window looked like.</summary>
    public int ResizeFrames { get; private set; }

    private bool _resizeFailureReported;

    private void OnFramebufferResize(Vector2D<int> size)
    {
        _fbSize = size;

        // Hand the resize to the tracker WITH a freshly-read scale. This callback is delivered from
        // inside the OS modal loop — the same reason the repaint below exists — so it is often the
        // first thing to witness a monitor change, arriving long before the frame tick gets a turn.
        // Reading the scale here rather than trusting the cached one is what tells a user resize
        // apart from a DPI transition; the previous version divided by the cached value and
        // corrupted the logical size, intermittently, depending on which event won the race.
        if (_scale is not null && !_resizingForDpi && _trackMonitorDpi)
        {
            var read = ReadDeviceScale();
            DpiTrace.Callback("resize", $"fb={size.X}x{size.Y} read={read:0.###} cached={_deviceScale:0.###}");
            ApplyDpiResize(_scale.ObserveFramebuffer(size.X, size.Y, read));
        }
        else DpiTrace.Callback("resize", $"fb={size.X}x{size.Y} (ours={_resizingForDpi})");
        // Surface is recreated lazily on the next frame at the new size.
        _surface?.Dispose(); _surface = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _forceRender = true;

        // Repaint NOW. Windows and macOS run a MODAL loop while a window edge is dragged: Run()'s
        // render loop does not get another turn until the mouse is released, so a frame left to
        // "next tick" arrives when the drag ENDS. GLFW still delivers this callback throughout, so
        // this is the only chance to draw mid-drag.
        try
        {
            _window?.DoRender();
            ResizeFrames++;
        }
        catch (Exception ex)
        {
            // Re-entrant rendering is refused on some platforms. That is survivable — the window
            // catches up on release — but it must not be SILENT: "resize is janky" needs to be
            // answerable from a log rather than by guesswork.
            if (!_resizeFailureReported)
            {
                _resizeFailureReported = true;
                Console.Error.WriteLine(
                    $"[CupriFace] live-resize repaint unavailable ({ex.GetType().Name}: {ex.Message}); " +
                    "the window will catch up when the drag ends.");
            }
        }

        if (ResizeDebug)
            Console.Error.WriteLine($"[resize] frame {ResizeFrames} at {size.X}x{size.Y}");
    }

    /// <summary>CUPRIFACE_RESIZE_DEBUG=1 traces every mid-drag repaint. One drag of a window edge
    /// then answers the question no CI machine can: does this actually stream?</summary>
    internal static readonly bool ResizeDebug =
        Environment.GetEnvironmentVariable("CUPRIFACE_RESIZE_DEBUG") is "1" or "true";

    private void EnsureSurface()
    {
        if (_surface is not null || _grContext is null) return;
        if (_fbSize.X <= 0 || _fbSize.Y <= 0) return;

        const uint GL_RGBA8 = 0x8058;
        var fbInfo = new GRGlFramebufferInfo(fboId: 0, format: GL_RGBA8);
        _renderTarget = new GRBackendRenderTarget(
            _fbSize.X, _fbSize.Y, sampleCount: 0, stencilBits: 8, fbInfo);
        _surface = SKSurface.Create(
            _grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
    }

    private void OnRender(double deltaSeconds)
    {
        // Before Tick, so anything the host does on the tick already sees the new scale. Polling
        // rather than hooking WM_DPICHANGED: GLFW owns this window's procedure, and subclassing it
        // to intercept one message is a far larger liability than one cheap query per frame.
        PollDeviceScale();
        Tick?.Invoke();

        EnsureSurface();
        if (_surface is null) return;

        var render = _forceRender || (ShouldRender?.Invoke() ?? true);
        _forceRender = false;
        if (render)
        {
            _stats.BeginFrame(deltaSeconds);

            Render?.Invoke(new RenderContext(_surface.Canvas, _fbSize.X, _fbSize.Y, _stats, _grContext));

            _grContext!.Flush(); // push the recorded draws to the GL framebuffer before swap

            // CUPRIFACE_FRAME_DUMP, on the GL window too. It existed only on the SDL software
            // window, which meant the path most people actually run could not be inspected at all
            // in a locked session or on CI - and a GPU-composited frame is exactly where a surface
            // texture can come out black while every managed assertion still passes.
            if (_frameDumpPath is { } dump && ++_presentCount % 15 == 0) DumpPresentedPixels(dump);

            _stats.EndFrame();
            _window!.SwapBuffers(); // manual swap: only drawn frames reach the screen
        }
        else
        {
            // Nothing changed: the front buffer stays as-is. The vsync wait lives in SwapBuffers,
            // which we skipped — sleep briefly so an idle window doesn't spin the render loop.
            System.Threading.Thread.Sleep(8);
        }

        if (ShouldClose?.Invoke(_stats) == true)
            _window!.Close();
    }

    // CUPRIFACE_FRAME_DUMP=<file.png>: read the pixels back FROM THE GPU SURFACE after the flush
    // and overwrite the file. Ground truth of what this frame contains, for environments where
    // OS-level screen capture is unavailable. Debug only, hence the env gate; every Nth frame so
    // the cost stays negligible.
    private readonly string? _frameDumpPath = Environment.GetEnvironmentVariable("CUPRIFACE_FRAME_DUMP");
    private int _presentCount;

    private void DumpPresentedPixels(string path)
    {
        try
        {
            if (_surface is null) return;
            using var img = _surface.Snapshot();
            // A GPU-backed snapshot has to come down to the CPU before it can be encoded.
            using var raster = img.ToRasterImage(ensurePixelData: true);
            using var data = raster.Encode(SKEncodedImageFormat.Png, 90);
            using var f = File.Create(path);
            data.SaveTo(f);
        }
        catch { /* diagnostics must never take the window down */ }
    }

    private void DisposeGpu()
    {
        _surface?.Dispose(); _surface = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _grContext?.Dispose(); _grContext = null;
        _glInterface?.Dispose(); _glInterface = null;
    }

    public void Dispose()
    {
        DisposeGpu();
        _input?.Dispose();
        _input = null;
        _window?.Dispose();
        _window = null;
    }
}

/// <summary>Everything a frame draw callback needs for one frame.</summary>
/// <param name="Gpu">The window's Skia GPU context, or null when this frame is being rasterised on
/// the CPU (the SDL software window). A surface producer that wants to skip the GPU-to-CPU-and-back
/// round trip needs this; everything else can ignore it. See <c>IGpuSurfaceSource</c>.</param>
public readonly record struct RenderContext(SKCanvas Canvas, int Width, int Height, FrameStats Stats,
                                            GRContext? Gpu = null);
