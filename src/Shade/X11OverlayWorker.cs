using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using static Shade.X11Native;

namespace Shade;

// Runs only in an isolated child process: Xlib's global handlers cannot interfere with CupriFace.
[SupportedOSPlatform("linux")]
internal sealed class X11OverlayWorker
{
    private sealed class Overlay(Display display, nuint window) { public Display Display = display; public nuint Window = window; public int Level; }
    private readonly SettingsStore? store;
    private readonly ShadeSettings settings;
    private readonly List<Overlay> overlays = [];
    private IReadOnlyList<Display> raw = Array.Empty<Display>();
    private readonly ConcurrentQueue<X11Request> requests = new();
    private readonly object wakeGate = new();
    private readonly ErrorHandler errorHandler;
    private readonly IoErrorHandler ioHandler;
    private nint display;
    private nuint root, controller, wakeAtom, opacityAtom;
    private int eventBase, nativeError;
    private int hotkeyError;
    private byte recoveryKey;
    private nuint recoverySymbol;
    private uint[] recoveryModifiers = [];
    private bool stopping, closing, hotkey, paused, dirty, focusUnsafe;
    private long restoreVersion;
    private string status = "Starting X11 overlays.";
    private Timer? saveTimer, retryTimer;

    private X11OverlayWorker(string? path)
    {
        store = path is null ? null : new(path); settings = store?.Load() ?? new();
        errorHandler = (_, error) => { Interlocked.Exchange(ref nativeError, Marshal.ReadByte(error, 32)); return 0; };
        ioHandler = _ => { Environment.Exit(5); return 0; };
    }
    public static int Run(string? path, bool testKey = false)
    {
        if (IntPtr.Size != 8) return 2; // The original event-union layout is currently verified on Linux x64.
        var worker = new X11OverlayWorker(path);
        try { worker.Loop(testKey); return 0; }
        catch (Exception) { return 4; }
        finally { worker.Close(); }
    }
    private void Check()
    {
        XSync(display, 0);
        if (Interlocked.Exchange(ref nativeError, 0) != 0) throw new InvalidOperationException("X11 request failed.");
    }
    private void Queue(X11Request request)
    {
        lock (wakeGate)
        {
            if (closing) return;
            requests.Enqueue(request);
            var value = new Event { Type = 33, Display = display, Window = controller, MessageType = wakeAtom, Format = 32 };
            XSendEvent(display, controller, 0, 0, ref value); XFlush(display);
        }
    }
    private void Loop(bool testKey)
    {
        if (XInitThreads() == 0) throw new NotSupportedException("Xlib thread support missing.");
        XSetErrorHandler(errorHandler); XSetIOErrorHandler(ioHandler);
        display = XOpenDisplay(0);
        if (display == 0) throw new InvalidOperationException("No X11 display.");
        root = XDefaultRootWindow(display);
        if (XShapeQueryVersion(display, out var major, out var minor) == 0 || major < 1 || (major == 1 && minor < 1))
            throw new NotSupportedException("X11 input shapes are required.");
        if (XRRQueryExtension(display, out eventBase, out _) == 0) throw new NotSupportedException("XRandR unavailable.");
        controller = XCreateSimpleWindow(display, root, 0, 0, 1, 1, 0, 0, 0);
        wakeAtom = XInternAtom(display, "_SHADE_WAKE", 0); opacityAtom = XInternAtom(display, "_NET_WM_WINDOW_OPACITY", 0);
        XRRSelectInput(display, root, 1 | 2 | 4 | 8);
        XSelectInput(display, root, 1 << 19); // SubstructureNotify: keep static overlays above newly mapped windows.
        Check();
        recoverySymbol = testKey ? 0xFFC8u : 0x72u; // F11 in isolated tests, R normally.
        ConfigureRecoveryHotkey();
        saveTimer = new(_ => Queue(new(0, "save")), null, Timeout.Infinite, Timeout.Infinite);
        retryTimer = new(_ => Queue(new(0, "refresh")), null, Timeout.Infinite, Timeout.Infinite);
        Reconcile(); Publish(0);
        var input = new Thread(() =>
        {
            try
            {
                while (Console.ReadLine() is { } line)
                {
                    if (line.Length > 2 * 1024 * 1024) break;
                    var request = JsonSerializer.Deserialize(line, ShadeJsonContext.Default.X11Request);
                    if (request is not null) Queue(request);
                }
            }
            catch (Exception) { }
            finally { Queue(new(0, "quit")); }
        }) { IsBackground = true, Name = "Shade X11 control pipe" };
        input.Start();
        while (!stopping)
        {
            XNextEvent(display, out var value);
            if (value.Type == 2 && value.Keycode == recoveryKey) { RestoreAll(); Publish(0); }
            else if (value.Type == 34 && value.MappingRequest is 0 or 1)
            {
                XRefreshKeyboardMapping(ref value);
                ConfigureRecoveryHotkey(); Reconcile(); Publish(0);
            }
            else if (value.Type == 9 && overlays.Any(o => o.Window == value.Window))
            {
                // Some remote/Xwayland compositors activate even input-disabled override-redirect windows.
                // Do not fight the user's focus or repeatedly remap a known incompatible surface.
                focusUnsafe = true; HideAll();
                status = "This compositor activated a non-interactive overlay. Shading is disabled to preserve keyboard input.";
                Publish(0);
            }
            else if (value.Type == eventBase || value.Type == eventBase + 1) { Reconcile(); Publish(0); }
            else if (value.Type == 19)
            {
                foreach (var overlay in overlays.Where(o => o.Level > 0)) XRaiseWindow(display, overlay.Window);
                XFlush(display);
            }
            while (requests.TryDequeue(out var request))
            {
                string? error = null;
                try { Execute(request); }
                catch (AssignmentException ex) { error = ex.Message; }
                catch (ArgumentException) { error = "Screen changed or dimming level was invalid."; }
                catch (InvalidOperationException ex) when (request.Operation == "verify") { error = ex.Message; }
                catch (Exception)
                {
                    HideAll(); paused = true; status = "X11 overlays paused after a native error; retrying automatically.";
                    retryTimer.Change(1000, Timeout.Infinite); error = status;
                }
                if (!stopping) Publish(request.Sequence, error);
            }
        }
    }
    private void ConfigureRecoveryHotkey()
    {
        var nextKey = XKeysymToKeycode(display, recoverySymbol);
        uint[] nextModifiers = [];
        hotkeyError = 0;
        try
        {
            nextModifiers = X11HotkeyModifiers.Read(display);
            if (nextKey == 0 || nextModifiers.Length == 0) throw new InvalidOperationException("Recovery gesture unavailable.");
            foreach (var modifiers in nextModifiers) XGrabKey(display, nextKey, modifiers, root, 0, 1, 1);
            XSync(display, 0); hotkeyError = Interlocked.Exchange(ref nativeError, 0);
            if (hotkeyError != 0) throw new InvalidOperationException("Recovery grab rejected.");
            // Keep the old recovery gesture until every replacement grab is accepted.
            foreach (var modifiers in recoveryModifiers)
                if (recoveryKey != nextKey || !nextModifiers.Contains(modifiers)) XUngrabKey(display, recoveryKey, modifiers, root);
            Check(); recoveryKey = nextKey; recoveryModifiers = nextModifiers; hotkey = true;
        }
        catch (Exception)
        {
            foreach (var modifiers in recoveryModifiers) XUngrabKey(display, recoveryKey, modifiers, root);
            if (nextKey != 0) foreach (var modifiers in nextModifiers) XUngrabKey(display, nextKey, modifiers, root);
            recoveryKey = nextKey; recoveryModifiers = []; hotkey = false;
            HideAll(); paused = true; XSync(display, 0); Interlocked.Exchange(ref nativeError, 0);
        }
    }
    private void Execute(X11Request request)
    {
        switch (request.Operation)
        {
            case "set-many":
                var changes = request.Levels ?? throw new ArgumentException();
                var targets = changes.Select(p => (Overlay: overlays.SingleOrDefault(o => o.Display.Id == p.Key)
                    ?? throw new ArgumentException(), Level: DimLevel.Validate(p.Value))).ToArray();
                if ((paused || !hotkey || focusUnsafe) && targets.Any(t => t.Level > 0)) throw new ArgumentException();
                foreach (var target in targets) { Apply(target.Overlay, target.Level); Remember(target.Overlay); }
                Check(); break;
            case "set":
                DimLevel.Validate(request.Level);
                if (focusUnsafe && request.Level > 0) throw new ArgumentException();
                if ((paused || !hotkey) && request.Level > 0) throw new InvalidOperationException();
                var overlay = overlays.SingleOrDefault(o => o.Display.Id == request.Id) ?? throw new ArgumentException();
                Apply(overlay, request.Level); Remember(overlay); Check(); break;
            case "preferences":
                var preferences = request.Preferences ?? throw new ArgumentException();
                settings.GlobalLevel = DimLevel.Validate(preferences.GlobalLevel);
                foreach (var d in overlays.Select(o => o.Display).Where(d => d.CanRemember))
                    if (preferences.Screens.TryGetValue(d.Id, out var control))
                    { DimLevel.Validate(control.IndividualLevel); settings.Controls[d.Id] = control; }
                ScheduleSave(); break;
            case "restore": RestoreAll(); break;
            case "refresh": if (!hotkey) ConfigureRecoveryHotkey(); Reconcile(); break;
            case "save": Save(); break;
            case "recover": store?.Recover(settings); break;
            case "assign":
                raw = X11DisplayCatalog.Read(display, settings.AmbiguousHardware).Displays;
                IdentityAssignments.Assign(settings, raw, request.Id ?? "", request.Name ?? "", request.ExistingId);
                ScheduleSave(); Reconcile(); break;
            case "detach": IdentityAssignments.Detach(settings, request.Id ?? ""); ScheduleSave(); Reconcile(); break;
            case "verify": Verify(); break;
            case "quit": stopping = true; break;
            default: throw new ArgumentException();
        }
    }
    private void ScheduleSave() { dirty = true; if (store is not null) saveTimer?.Change(750, Timeout.Infinite); }
    private void Save() { if (dirty && store is not null) { store.Save(settings); dirty = false; } }
    private void Remember(Overlay overlay)
    {
        var d = overlay.Display;
        if (d.CanRemember) { settings.Displays[d.Id] = new(overlay.Level, d.X, d.Y, d.Width, d.Height); ScheduleSave(); }
    }
    private void HideAll()
    {
        foreach (var overlay in overlays) { XUnmapWindow(display, overlay.Window); overlay.Level = 0; }
        XFlush(display);
    }
    private void RestoreAll()
    {
        HideAll(); restoreVersion++;
        foreach (var id in settings.Controls.Keys.ToArray()) settings.Controls[id] = settings.Controls[id] with { Enabled = false };
        foreach (var id in settings.Displays.Keys.ToArray()) settings.Displays[id] = settings.Displays[id] with { Level = 0 };
        ScheduleSave();
    }
    private void Apply(Overlay overlay, int level)
    {
        if (overlay.Level == level) return;
        if (level == 0) XUnmapWindow(display, overlay.Window);
        else
        {
            var opacity = (nint)(uint)Math.Round(uint.MaxValue * (level / 100d));
            XChangeProperty(display, overlay.Window, opacityAtom, 6, 32, 0, [opacity], 1); // CARDINAL, replace
            if (overlay.Level == 0) XMapRaised(display, overlay.Window);
        }
        overlay.Level = level;
    }
    private void Reconcile()
    {
        try
        {
            var catalog = X11DisplayCatalog.Read(display, settings.AmbiguousHardware); Check();
            if (!catalog.Compositor) throw new NotSupportedException("No X11 compositor.");
            raw = catalog.Displays;
            var displays = raw.Select(d => IdentityAssignments.Resolve(d, settings.Assignments)).ToArray();
            foreach (var old in overlays.Where(o => !displays.Any(d => d.Id == o.Display.Id)).ToArray())
            { XDestroyWindow(display, old.Window); overlays.Remove(old); }
            foreach (var d in displays)
            {
                var overlay = overlays.SingleOrDefault(o => o.Display.Id == d.Id);
                var desired = overlay?.Level ?? 0;
                if (overlay is null || paused)
                    if (d.CanRemember) desired = settings.Controls.TryGetValue(d.Id, out var saved)
                        ? saved.EffectiveLevel(settings.GlobalLevel) : settings.Displays.GetValueOrDefault(d.Id)?.Level ?? 0;
                if (overlay is null)
                {
                    var window = XCreateSimpleWindow(display, root, d.X, d.Y, (uint)d.Width, (uint)d.Height, 0, 0, 0);
                    var attributes = new Attributes { OverrideRedirect = 1 };
                    XChangeWindowAttributes(display, window, 1u << 9, ref attributes);
                    var hints = new Hints { Flags = 1, Input = 0 }; XSetWMHints(display, window, ref hints);
                    XStoreName(display, window, "Shade overlay");
                    XSelectInput(display, window, 1 << 21); // FocusChange: fail closed on incompatible compositors.
                    XShapeCombineRectangles(display, window, 2, 0, 0, 0, 0, 0, 0); // Empty ShapeInput region.
                    XChangeProperty(display, window, XInternAtom(display, "_NET_WM_STATE", 0), 4, 32, 0,
                        [(nint)XInternAtom(display, "_NET_WM_STATE_ABOVE", 0), (nint)XInternAtom(display, "_NET_WM_STATE_SKIP_TASKBAR", 0)], 2);
                    overlay = new(d, window); overlays.Add(overlay);
                }
                else if (overlay.Display != d)
                { XMoveResizeWindow(display, overlay.Window, d.X, d.Y, (uint)d.Width, (uint)d.Height); overlay.Display = d; }
                if (hotkey && !focusUnsafe) { Apply(overlay, desired); Remember(overlay); }
            }
            Check(); paused = !hotkey; retryTimer?.Change(hotkey ? Timeout.Infinite : 1000, Timeout.Infinite);
            status = focusUnsafe ? "This compositor activated a non-interactive overlay. Shading is disabled to preserve keyboard input."
                : hotkey ? "X11 overlays active. Ctrl+Alt+Shift+R restores every screen. Native Wayland surfaces are not covered."
                : $"Recovery hotkey unavailable (X11 error {hotkeyError}, keycode {recoveryKey}). X11 dimming is disabled.";
            ScheduleSave();
        }
        catch (Exception)
        {
            HideAll(); paused = true; status = "X11 display configuration or compositor unavailable; shading paused and retrying.";
            retryTimer?.Change(1000, Timeout.Infinite);
        }
    }
    private void Publish(long sequence, string? error = null)
    {
        var state = new X11State(overlays.Select(o => o.Display).ToArray(), overlays.ToDictionary(o => o.Display.Id, o => o.Level),
            new(settings.GlobalLevel, new Dictionary<string, RememberedControl>(settings.Controls)),
            settings.Assignments.Select(p => new AssignmentChoice(p.Key, p.Value.Name, raw.Any(d => d.Id == p.Value.ConnectionId))).ToArray(),
            restoreVersion, store?.Error ?? status, store?.Error is not null, store?.PreservingUnreadableFile == true);
        Console.WriteLine(JsonSerializer.Serialize(new X11Response(sequence, state, error), ShadeJsonContext.Default.X11Response)); Console.Out.Flush();
    }
    private void Verify()
    {
        XGetInputFocus(display, out var before, out _);
        foreach (var overlay in overlays)
        {
            if (XGetGeometry(display, overlay.Window, out _, out var x, out var y, out var width, out var height, out _, out _) == 0)
                throw new InvalidOperationException();
            var d = overlay.Display;
            if (x != d.X || y != d.Y || width != d.Width || height != d.Height) throw new InvalidOperationException("Bounds mismatch.");
            var rectangles = XShapeGetRectangles(display, overlay.Window, 2, out var count, out _);
            if (rectangles != 0) XFree(rectangles);
            if (count != 0) throw new InvalidOperationException("Overlay accepts pointer input.");
            if (before == overlay.Window) throw new InvalidOperationException($"Overlay stole keyboard focus (level {overlay.Level}).");
        }
        Check();
    }
    private void Close()
    {
        lock (wakeGate) closing = true;
        saveTimer?.Dispose(); retryTimer?.Dispose();
        if (display != 0)
        {
            foreach (var overlay in overlays) XDestroyWindow(display, overlay.Window);
            if (controller != 0) XDestroyWindow(display, controller);
            XCloseDisplay(display); display = 0;
        }
        Save(); GC.KeepAlive(errorHandler); GC.KeepAlive(ioHandler);
    }
}
