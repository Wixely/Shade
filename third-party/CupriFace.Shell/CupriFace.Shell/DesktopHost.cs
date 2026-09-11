using System.Diagnostics;
using CupriFace;
using CupriFace.Hosting;
using CupriFace.Interaction;

namespace CupriFace.Shell;

/// <summary>
/// Runs a <see cref="CupriApp"/> in a desktop window: tries GPU (GL), falls back to the
/// SDL software window, and wires pointer input to the app's document. Its siblings render the
/// *same* app elsewhere: <c>WebHost.Run</c> (CupriFace.Web.Mono) to a browser &lt;canvas&gt;, and
/// <c>CupriActivity</c> (CupriFace.Android) to a GL surface on a phone.
/// </summary>
public static class DesktopHost
{
    /// <summary>
    /// DIAGNOSTICS ONLY: the scale state the last drawn frame used — D, the framebuffer, the logical
    /// client size and T. Exposed so a probe app can display what the host actually computed rather
    /// than re-deriving it and agreeing with itself. Not a supported API for app logic: an app is
    /// told its size through <see cref="CupriApp.Present"/>, in logical units, on purpose.
    /// </summary>
    public static HostScale FrameScale { get; private set; }

    /// <summary>DIAGNOSTICS ONLY: what became of the Per-Monitor-V2 request at startup. "granted"
    /// means this process established it; "already set" means a manifest or the app got there first
    /// and won, which is correct behaviour rather than a failure.</summary>
    public static string DpiAwareness { get; private set; } = "not requested (non-Windows)";

    /// <param name="app">The portable application definition.</param>
    /// <param name="configure">Host-composition hook, run once after the document is built —
    /// where desktop-only capabilities attach (e.g. <c>d =&gt; d.UseVideo(new WebmVideoBackend())</c>
    /// from the optional CupriFace.Media package). Kept OUT of <see cref="CupriApp.Configure"/> on
    /// purpose: the app class is shared with hosts that must not reference desktop codecs.</param>
    public static void Run(CupriApp app, Action<CupriDocument>? configure = null)
    {
        // Per-Monitor-V2, before ANY window can exist — awareness is a process property that windows
        // inherit at creation, so this is the only moment it can be declared. Ahead of the GL probe
        // on purpose: the probe is a second process running this same line, and a probe window with
        // different awareness to the real one is not the thing being probed.
        //
        // A REQUEST, not a demand: if the executable's manifest already declares an awareness, or
        // the app set one before calling here, Windows refuses and that choice stands (#137).
        if (OperatingSystem.IsWindows())
        {
            DpiAwareness = !app.DpiAware ? "off (CupriApp.DpiAware = false)"
                : !WindowsDpi.Enabled ? "off (CUPRIFACE_DPI=0)"
                : WindowsDpi.TryDeclarePerMonitorV2() ? "Per-Monitor-V2 (granted)"
                : "already set by the app's manifest or the app itself — that choice wins";
        }

        // The GL-probe child (see GlProbeSurvives): attempt GL bring-up, report via exit code,
        // never open the real window. Checked before anything else so the probe stays invisible.
        if (Environment.GetCommandLineArgs().Contains("--cupriface-gl-probe"))
        {
            try { SkiaWindow.Probe(); Environment.Exit(0); }
            catch { Environment.Exit(1); }
        }

        var doc = app.CreateDocument();
        configure?.Invoke(doc);
        // External links (http/mailto/…) open in the OS browser; internal routing + #anchors are the
        // app's / engine's concern. Both hosts do this, so links behave the same on desktop and web.
        doc.Navigated += e => { if (e.External) OpenExternal(e.Href); };
        // Decode the app icon once (any size PNG/JPEG → RGBA8888); both window kinds take raw pixels.
        (byte[] Rgba, int W, int H)? icon = null;
        if (app.Icon is { Length: > 0 } iconBytes)
        {
            using var bmp = SkiaSharp.SKBitmap.Decode(iconBytes);
            if (bmp is not null)
            {
                using var rgba = bmp.Copy(SkiaSharp.SKColorType.Rgba8888);
                if (rgba is not null) icon = (rgba.Bytes, rgba.Width, rgba.Height);
            }
        }

        var clock = Stopwatch.StartNew();
        // The three scales, kept apart (#137). `scale` is P — the APPLICATION's factor, and the only
        // one pointer coordinates need, because both windows now hand this host logical client units
        // already. `effective` is T = D*P — the one the canvas, the surfaces, the damage rectangle
        // and the accessibility geometry all use. `deviceScale` reads D off whichever window opened.
        var scale = 1f;
        var effective = 1f;
        Func<float> deviceScale = () => 1f;
        var logicalW = 0f; var logicalH = 0f; // last presented logical size, for the a11y snapshot
        var lastRefresh = 0.0;

        // Render-on-demand (same model as the WASM host): input marks the doc dirty only when a
        // dispatch actually changed something; animation/refresh/image arrival wake it too. A static
        // window renders nothing at all.
        var dirty = true;
        void Mark(bool changed) { if (changed) dirty = true; }
        bool NeedsRender()
        {
            // Periodic re-bind so live computed values (e.g. diagnostics) update on their own.
            if (app.RefreshIntervalSeconds > 0 &&
                clock.Elapsed.TotalSeconds - lastRefresh >= app.RefreshIntervalSeconds)
            {
                lastRefresh = clock.Elapsed.TotalSeconds;
                doc.Refresh();
                dirty = true;
            }
            if (doc.ConsumeImageArrived()) dirty = true;      // a background image finished loading
            if (doc.HasActiveAnimations) dirty = true;        // keyframes/transitions/toasts running
            var d = dirty;
            dirty = false;
            return d;
        }

        void Draw(RenderContext ctx)
        {
            // The framebuffer is PHYSICAL pixels; the application is asked about the LOGICAL window
            // it occupies. Handing an app framebuffer pixels was the bug: on a 150% monitor it laid
            // out as though it had half again as much room, so everything came out physically small
            // and did not keep its size when the window moved to another display (#137).
            var host = HostScale.ForFramebuffer(ctx.Width, ctx.Height, deviceScale());
            var p = app.Present(host.LogicalClientWidth, host.LogicalClientHeight);
            scale = p.Scale <= 0 ? 1f : p.Scale;
            var resolved = host.WithPresentScale(scale);
            effective = resolved.EffectiveScale;
            FrameScale = resolved;                      // diagnostics; see the property
            logicalW = p.LogicalWidth; logicalH = p.LogicalHeight;
            // Tell surfaces what a logical pixel is worth before asking any of them to draw. A
            // producer that rasterises to order — a GL viewport — sizes its buffer from this, and
            // without it renders at logical resolution and is upscaled into its box. It is T rather
            // than P: the monitor's contribution is exactly as real as the application's.
            doc.Surfaces.DeviceScale = effective;

            // GPU surface producers go FIRST, before a single command is recorded for this frame.
            // They issue raw GL on the same context Skia is about to use, so doing it mid-recording
            // would corrupt the state Skia believes the driver is in; the registry calls
            // ResetContext afterwards. A software window passes null here and producers fall back
            // to publishing ordinary SKImages. Present() above only computes — it records nothing —
            // so it is safely on this side of that line.
            if (ctx.Gpu is { } gpu) doc.Surfaces.RenderGpuFrames(gpu);

            // Transparent apps clear to a fully-transparent framebuffer so the desktop shows through
            // (premultiplied output is exactly what the OS compositor wants — no conversion needed).
            ctx.Canvas.Clear(app.Transparent ? SkiaSharp.SKColors.Transparent : app.Background);
            if (doc.HasAnimations || doc.HasActiveTransitions)
                doc.Animate(clock.Elapsed.TotalSeconds); // drive @keyframes (spinner) + CSS transitions

            ctx.Canvas.Save();
            if (effective != 1f) ctx.Canvas.Scale(effective);
            doc.Render(ctx.Canvas, p.LogicalWidth, p.LogicalHeight);
            ctx.Canvas.Restore();
        }

        // Escape hatch: CUPRIFACE_SOFTWARE=1 skips the GL attempt entirely and goes straight to
        // the SDL software window, which renders the same pixels a little slower. The GL path's
        // known failure modes are handled these days (a broken GL stack raises an ordinary
        // exception and falls through to SDL below) — but an explicit override beats debugging.
        var forceSoftware = Environment.GetEnvironmentVariable("CUPRIFACE_SOFTWARE") is "1" or "true" or "TRUE";

        // macOS with no OpenGL at all (the paravirtual GPU of virtualised Macs — CI runners, UTM
        // guests) kills the process NATIVELY inside GLFW before any managed guard can run: window
        // creation fails without setting a GLFW error, Silk.NET applies the default position to
        // the NULL handle, and release-build GLFW segfaults in glfwSetWindowPos (the macOS CI
        // crash report named that exact frame, window argument = 0). Uncatchable in-process — so
        // a throwaway child process takes the risk first. Real Macs (GL present) pay one
        // invisible ~200 ms probe at startup and then get the GPU window as before.
        if (!forceSoftware && OperatingSystem.IsMacOS() && !GlProbeSurvives())
        {
            Console.WriteLine("[CupriFace] GL probe failed (no OpenGL here); using the SDL software window.");
            forceSoftware = true;
        }

        try
        {
            if (forceSoftware)
                throw new InvalidOperationException("CUPRIFACE_SOFTWARE is set; skipping the GL window.");

            var window = new SkiaWindow(
                app.Title,
                app.Width,
                app.Height,
                app.Transparent,
                app.Frameless,
                app.TopMost,
                app.DarkWindowChrome,
                app.Background,
                app.DpiAware,
                app.TrackMonitorDpi);
            if (icon is { } ic) window.SetIcon(ic.Rgba, ic.W, ic.H);
            deviceScale = () => window.DeviceScale;
            // A monitor change resizes every raster-backed surface and invalidates the retained
            // frame: what was cached was rasterised for the old scale and is the wrong pixel count
            // for the new one.
            window.DeviceScaleChanged += _ => { doc.InvalidateRetainedFrame(); dirty = true; };
            window.ShouldRender = NeedsRender; // GL: skip draw + swap entirely on clean frames
            window.Render += Draw;

            // Accessibility: whichever bridge this OS has (UIA on Windows, AT-SPI on Linux),
            // attached on the first tick that can (Windows needs the HWND), draining queued AT
            // actions on this UI thread, and publishing a semantics snapshot after each drawn
            // frame — the subscription order after Draw is what sequences that. No-ops on a
            // platform without a bridge, under its kill switch, or if attaching failed.
            using var a11y = new Accessibility.PlatformAccessibility(doc, () => dirty = true, app.Title);
            window.Tick += () => { if (a11y.Tick(() => OperatingSystem.IsMacOS() ? window.CocoaWindow : window.Win32Hwnd)) dirty = true; };
            // T, not P: an AT is told where things are in PHYSICAL screen pixels, so the monitor's
            // scale belongs in the same multiply the canvas used. Publishing P alone put every
            // bounding rectangle at two-thirds size on a 150% display.
            window.Render += _ => a11y.Publish(logicalW, logicalH, effective, window.ScreenPosition);
            using var tray = new WindowsTrayIcon(app.CloseToTray, app.Title, app.TrayCloseLabel);
            var topMost = app.TopMost;
            window.Tick += () =>
            {
                if (app.TopMost != topMost)
                {
                    topMost = app.TopMost;
                    window.SetTopMost(topMost);
                }
                tray.Attach(window.Win32Hwnd);
            };

            window.PointerDown += (x, y, clicks) =>
            {
                var logicalX = x / scale;
                var logicalY = y / scale;
                Mark(DesktopPointerDown(doc, logicalX, logicalY, clicks));
                window.SetCursor(doc.CursorAt(logicalX, logicalY));
            };
            window.RightPointerDown += (x, y) => Mark(doc.DispatchContextMenu(x / scale, y / scale));
            window.PointerMove += (x, y) =>
            {
                var logicalX = x / scale;
                var logicalY = y / scale;
                Mark(DesktopPointerMove(doc, logicalX, logicalY));
                window.SetCursor(doc.CursorAt(logicalX, logicalY));
            };
            window.PointerUp += (x, y) =>
            {
                var logicalX = x / scale;
                var logicalY = y / scale;
                Mark(DesktopPointerUp(doc, logicalX, logicalY));
                window.SetCursor(doc.CursorAt(logicalX, logicalY));
            };
            window.PointerWheel += (x, y, dy, mods) =>
            {
                // Ctrl+wheel is zoom — one ladder rung per notch, as every browser has it. A plain
                // wheel scrolls. The split lives here so the engine never learns chord conventions.
                // Anchored at the pointer, because a wheel zoom HAS a pointer — the user is
                // pointing at the thing they want to look at more closely.
                if (mods.HasFlag(KeyMods.Ctrl))
                {
                    if (dy > 0) doc.ZoomIn(x / scale, y / scale);
                    else if (dy < 0) doc.ZoomOut(x / scale, y / scale);
                    dirty = true;
                }
                else Mark(doc.DispatchWheel(x / scale, y / scale, -dy * 50f)); // wheel up → scroll up
            };
            window.TextEntered += t => Mark(doc.DispatchKey(t, EditKey.None));
            window.EditKeyPressed += (k, mods) =>
            {
                var handled = doc.DispatchKey(null, k, mods);
                Mark(handled);
                // Escape the document didn't consume (no overlay open) exits fullscreen — the OS
                // convention. Overlays keep winning: dismissing one returns handled above.
                if (!handled && k == EditKey.Escape && window.IsFullscreen) window.SetFullscreen(false);
            };
            window.Shortcut += (ch, mods) => { Shortcut(doc, ch, mods, () => window.ClipboardText, v => window.ClipboardText = v); dirty = true; };
            doc.ContextRequested += cmd => { ContextAction(doc, cmd, () => window.ClipboardText, v => window.ClipboardText = v); dirty = true; };
            doc.WindowCommandRequested += cmd => window.SetFullscreen(cmd switch
            {
                WindowCommand.EnterFullscreen => true,
                WindowCommand.ExitFullscreen => false,
                _ => !window.IsFullscreen,
            });
            // A frameless window has no title bar to grab, so an element marked data-window-drag
            // becomes one. Wired on BOTH window types — a host feature added to only one of them is
            // how the GL and SDL paths have drifted before.
            doc.WindowMoveRequested += m => window.MoveBy(m.Dx, m.Dy);
            Action<string> clipboardWriter = value => window.ClipboardText = value;
            app.ClipboardWriteRequested += clipboardWriter;
            try { window.Run(); }
            finally { app.ClipboardWriteRequested -= clipboardWriter; }
        }
        catch (Exception ex)
        {
            // The message, not just the type: a bare "InvalidOperationException" here was read as a
            // driverless machine when it was a harness forcing the software path, and a bare
            // "PlatformNotSupportedException" as a session limit when it was the trimmer removing
            // Silk.NET's backends (#125, #126). The line is the only witness a fallback leaves.
            Console.WriteLine($"[CupriFace] GPU unavailable ({ex.GetType().Name}: {ex.Message}); using the SDL software window.");
            using var window = new SdlSoftwareWindow(
                app.Title,
                app.Width,
                app.Height,
                app.Transparent,
                app.Frameless,
                app.TopMost,
                app.DarkWindowChrome,
                app.Background,
                app.DpiAware,
                app.TrackMonitorDpi);
            if (icon is { } ic) window.SetIcon(ic.Rgba, ic.W, ic.H);
            deviceScale = () => window.DeviceScale;

            // The retained surface was recreated (blank): the doc's damage diff must restart from
            // a full repaint, and the frame must actually render even if nothing else is dirty.
            // A monitor change goes through here too — PollDeviceScale recreates the surface at the
            // new pixel size, so the same invalidation covers both.
            window.SurfaceRecreated += () => { doc.InvalidateRetainedFrame(); dirty = true; };

            // The same bridge on the software window — this is the path GL-less machines (RDP,
            // VMs, CI runners, and every headless Linux box) actually take, so assistive tech
            // must work here, not only on GL.
            using var a11y = new Accessibility.PlatformAccessibility(doc, () => dirty = true, app.Title);
            window.Tick += () => { if (a11y.Tick(() => OperatingSystem.IsMacOS() ? window.CocoaWindow : window.Win32Hwnd)) dirty = true; };
            using var tray = new WindowsTrayIcon(app.CloseToTray, app.Title, app.TrayCloseLabel);
            var topMost = app.TopMost;
            window.Tick += () =>
            {
                if (app.TopMost != topMost)
                {
                    topMost = app.TopMost;
                    window.SetTopMost(topMost);
                }
                tray.Attach(window.Win32Hwnd);
            };

            // Commit-snapshot render thread (opt-in): build the display list on this UI thread and let
            // a background thread rasterise it; present the latest completed frame each vsync. Targets
            // the physical surface (scale 1), so it composes with the responsive present.
            using var presenter = app.ThreadedRender ? new CupriFace.Threading.ThreadedPresenter() : null;
            void DrawThreaded(RenderContext ctx)
            {
                presenter!.Present(ctx.Canvas); // draw the previous frame the render thread finished
                if (app.RefreshIntervalSeconds > 0 && clock.Elapsed.TotalSeconds - lastRefresh >= app.RefreshIntervalSeconds)
                { lastRefresh = clock.Elapsed.TotalSeconds; doc.Refresh(); }
                if (doc.HasAnimations || doc.HasActiveTransitions) doc.Animate(clock.Elapsed.TotalSeconds);

                // This path used never to call app.Present at all — so it ignored Hybrid and Zoom as
                // well as DPI, and published accessibility geometry at a hard-coded scale 1. It now
                // computes exactly what the inline paths do; only the rasterisation is elsewhere.
                var host = HostScale.ForFramebuffer(ctx.Width, ctx.Height, deviceScale());
                var p = app.Present(host.LogicalClientWidth, host.LogicalClientHeight);
                scale = p.Scale <= 0 ? 1f : p.Scale;
                var resolved = host.WithPresentScale(scale);
                effective = resolved.EffectiveScale;
                FrameScale = resolved;
                doc.Surfaces.DeviceScale = effective;

                // The list is built in LOGICAL units and the render thread scales it into the device
                // surface, so the glyphs are rasterised at the size they are shown at.
                var list = doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
                presenter.Submit(list, ctx.Width, ctx.Height,
                                 app.Transparent ? SkiaSharp.SKColors.Transparent : app.Background, effective);
                a11y.Publish(p.LogicalWidth, p.LogicalHeight, effective, window.ScreenPosition);
            }

            if (presenter is not null)
                window.Render += DrawThreaded; // threaded path keeps its own pipeline (no damage/skip)
            else
            {
                // Damage-aware render-on-demand: repaint only the changed rect of the retained bitmap;
                // a clean frame renders, uploads, and presents nothing.
                window.RenderIncrementalFrame = ctx =>
                {
                    if (!NeedsRender()) return null;
                    // Same split as the GL path: the app is asked about its LOGICAL window, and the
                    // canvas, the damage rect and the a11y geometry all use T = D*P (#137).
                    var host = HostScale.ForFramebuffer(ctx.Width, ctx.Height, deviceScale());
                    var p = app.Present(host.LogicalClientWidth, host.LogicalClientHeight);
                    scale = p.Scale <= 0 ? 1f : p.Scale;
                    var resolved = host.WithPresentScale(scale);
                    effective = resolved.EffectiveScale;
                    FrameScale = resolved;
                    doc.Surfaces.DeviceScale = effective;
                    if (doc.HasAnimations || doc.HasActiveTransitions) doc.Animate(clock.Elapsed.TotalSeconds);
                    var bg = app.Transparent ? SkiaSharp.SKColors.Transparent : app.Background;

                    // Scale the canvas, then damage-clip inside it: the engine's clip is interpreted
                    // in the scaled space (= logical space), so only the rectangle it returns needs
                    // converting to device pixels. Previously any scale but 1 repainted in full,
                    // which on a HiDPI or fractionally-scaled display is every frame (#99).
                    ctx.Canvas.Save();
                    if (effective != 1f) ctx.Canvas.Scale(effective);
                    var logical = doc.RenderIncremental(ctx.Canvas, p.LogicalWidth, p.LogicalHeight, bg);
                    ctx.Canvas.Restore();
                    SkiaSharp.SKRectI? damage = logical is { } lg
                        ? CupriDocument.ScaleDamageToDevice(lg, effective, ctx.Width, ctx.Height)
                        : null;
                    // A drawn frame is the moment the tree is laid out and current — publish then.
                    if (damage is not null) a11y.Publish(p.LogicalWidth, p.LogicalHeight, effective, window.ScreenPosition);
                    return damage;
                };
            }
            window.PointerDown += (x, y, clicks) =>
            {
                var logicalX = x / scale;
                var logicalY = y / scale;
                Mark(DesktopPointerDown(doc, logicalX, logicalY, clicks));
                window.SetCursor(doc.CursorAt(logicalX, logicalY));
            };
            window.RightPointerDown += (x, y) => Mark(doc.DispatchContextMenu(x / scale, y / scale));
            window.PointerMove += (x, y) =>
            {
                var logicalX = x / scale;
                var logicalY = y / scale;
                Mark(DesktopPointerMove(doc, logicalX, logicalY));
                window.SetCursor(doc.CursorAt(logicalX, logicalY));
            };
            window.PointerUp += (x, y) =>
            {
                var logicalX = x / scale;
                var logicalY = y / scale;
                Mark(DesktopPointerUp(doc, logicalX, logicalY));
                window.SetCursor(doc.CursorAt(logicalX, logicalY));
            };
            window.PointerWheel += (x, y, dy, mods) =>
            {
                // Ctrl+wheel is zoom — one ladder rung per notch, as every browser has it. A plain
                // wheel scrolls. The split lives here so the engine never learns chord conventions.
                // Anchored at the pointer, because a wheel zoom HAS a pointer — the user is
                // pointing at the thing they want to look at more closely.
                if (mods.HasFlag(KeyMods.Ctrl))
                {
                    if (dy > 0) doc.ZoomIn(x / scale, y / scale);
                    else if (dy < 0) doc.ZoomOut(x / scale, y / scale);
                    dirty = true;
                }
                else Mark(doc.DispatchWheel(x / scale, y / scale, -dy * 50f)); // wheel up → scroll up
            };
            window.TextEntered += t => Mark(doc.DispatchKey(t, EditKey.None));
            window.EditKeyPressed += (k, mods) =>
            {
                var handled = doc.DispatchKey(null, k, mods);
                Mark(handled);
                if (!handled && k == EditKey.Escape && window.IsFullscreen) window.SetFullscreen(false);
            };
            window.Shortcut += (ch, mods) => { Shortcut(doc, ch, mods, () => window.ClipboardText, v => window.ClipboardText = v); dirty = true; };
            doc.ContextRequested += cmd => { ContextAction(doc, cmd, () => window.ClipboardText, v => window.ClipboardText = v); dirty = true; };
            doc.WindowCommandRequested += cmd => window.SetFullscreen(cmd switch
            {
                WindowCommand.EnterFullscreen => true,
                WindowCommand.ExitFullscreen => false,
                _ => !window.IsFullscreen,
            });
            // A frameless window has no title bar to grab, so an element marked data-window-drag
            // becomes one. Wired on BOTH window types — a host feature added to only one of them is
            // how the GL and SDL paths have drifted before.
            doc.WindowMoveRequested += m => window.MoveBy(m.Dx, m.Dy);
            Action<string> clipboardWriter = value => window.ClipboardText = value;
            app.ClipboardWriteRequested += clipboardWriter;
            try { window.Run(); }
            finally { app.ClipboardWriteRequested -= clipboardWriter; }
        }
    }

    // Raw-pointer elements get first refusal so a desktop mouse can drive the same captured hold /
    // drag interactions as touch. Everything else keeps the ordinary click/hover/drag path.
    private static bool DesktopPointerDown(CupriDocument doc, float x, float y, int clickCount) =>
        doc.DispatchPointer(0, PointerPhase.Down, x, y) || doc.DispatchClick(x, y, clickCount);

    private static bool DesktopPointerMove(CupriDocument doc, float x, float y) =>
        doc.IsPointerCaptured(0)
            ? doc.DispatchPointer(0, PointerPhase.Move, x, y)
            : doc.DispatchPointerMove(x, y);

    private static bool DesktopPointerUp(CupriDocument doc, float x, float y) =>
        doc.IsPointerCaptured(0)
            ? doc.DispatchPointer(0, PointerPhase.Up, x, y)
            : doc.DispatchPointerUp(x, y);

    // Launch ourselves with --cupriface-gl-probe and read the verdict off the exit code: 0 means the
    // child brought GL up end to end; anything else — a managed throw, a native SIGSEGV, a hang —
    // means this machine doesn't get the GL window. Any doubt (no process path, spawn failure,
    // timeout) counts as failure: the fallback is a working window either way.
    private static bool GlProbeSurvives()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;
            // Under `dotnet run` / `dotnet Viewer.dll` the process path is the dotnet host, which
            // would swallow the flag — re-exec the entry assembly through it. Published apps
            // (apphost or single-file) re-exec themselves.
            var entry = Environment.GetCommandLineArgs().FirstOrDefault();
            var viaHost = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                          && entry is { Length: > 0 };
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (viaHost) { psi.ArgumentList.Add("exec"); psi.ArgumentList.Add(entry!); }
            psi.ArgumentList.Add("--cupriface-gl-probe");

            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // Open an allowed external link in the OS default browser/handler. Re-check at the host boundary
    // so no future call site can hand file:, javascript:, or a custom executable protocol to the shell.
    private static void OpenExternal(string href)
    {
        if (!ExternalLinkPolicy.IsAllowed(href)) return;
        try { Process.Start(new ProcessStartInfo(href) { UseShellExecute = true }); }
        catch { /* no handler / malformed url — ignore */ }
    }

    // Text shortcuts common to both hosts. The engine owns selection/editing; the host owns the
    // OS clipboard (get/set passed in) — keeping the engine free of any platform clipboard code.
    private static void Shortcut(CupriDocument doc, char ch, KeyMods mods, Func<string?> getClip, Action<string> setClip)
    {
        switch (ch)
        {
            case 'a': doc.DispatchKey(null, EditKey.SelectAll); break;
            case 'c': if (doc.CopySelection() is { } cp) setClip(cp); break;
            case 'x': if (doc.CutSelection() is { } ct) setClip(ct); break;
            case 'v': if (getClip() is { Length: > 0 } pv) doc.DispatchKey(pv, EditKey.None); break;
            case 'z': if (mods.HasFlag(KeyMods.Shift)) doc.Redo(); else doc.Undo(); break; // Ctrl+Shift+Z = redo
            case 'y': doc.Redo(); break;
            // Page zoom, browser keys. The chrome owns these the way a browser does — they never
            // reach the app's OnShortcut. (The windows only send =/-/0 as chords for this purpose.)
            case '=': doc.ZoomIn(); break;
            case '-': doc.ZoomOut(); break;
            case '0': doc.ZoomReset(); break;
            // Anything else is the app's own shortcut (doc.OnShortcut) — e.g. Ctrl+K opening a command
            // palette. The web host has always forwarded these via KeyChord; the desktop hosts dropped
            // them, so a documented feature only worked in the browser.
            default: doc.DispatchKey(ch.ToString(), EditKey.None, mods); break;
        }
    }

    // A context-menu command routes through the SAME clipboard seam as the keyboard shortcuts.
    private static void ContextAction(CupriDocument doc, ContextCommand cmd, Func<string?> getClip, Action<string> setClip)
    {
        switch (cmd)
        {
            case ContextCommand.Cut: if (doc.CutSelection() is { } ct) setClip(ct); break;
            case ContextCommand.Copy: if (doc.CopySelection() is { } cp) setClip(cp); break;
            case ContextCommand.Paste: if (getClip() is { Length: > 0 } pv) doc.DispatchKey(pv, EditKey.None); break;
            case ContextCommand.SelectAll: doc.DispatchKey(null, EditKey.SelectAll); break;
        }
    }
}
