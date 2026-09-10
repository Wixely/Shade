using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Shade.WindowsNative;

namespace Shade;

[SupportedOSPlatform("windows")]
public sealed class WindowsDimmingBackend : IDimmingBackend
{
    private sealed class Overlay(Display display, nint window)
    {
        public Display Display { get; } = display;
        public nint Window { get; } = window;
        public int Level;
    }
    private readonly object gate = new();
    private readonly Queue<(Action Action, TaskCompletionSource Completion)> commands = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly WindowProcedure procedure;
    private readonly List<Overlay> overlays = [];
    private readonly string className = "Shade.Overlay." + Guid.NewGuid().ToString("N");
    private nint controller;
    private bool stopping, unavailable, restoreRequested, topologyChanged;
    private bool disposed;
    private Exception? failure;
    private volatile string status = "Starting overlays...";
    public IReadOnlyList<Display> Displays { get; private set; } = [];
    public string Status => status;

    public WindowsDimmingBackend()
    {
        procedure = WindowProc;
        thread = new Thread(Run) { IsBackground = true, Name = "Shade overlays" };
        thread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { thread.Join(); throw; }
    }

    public int GetLevel(string id)
    {
        var overlay = overlays.SingleOrDefault(o => o.Display.Id == id)
            ?? throw new ArgumentException("Unknown display.", nameof(id));
        return Volatile.Read(ref overlay.Level);
    }

    public void SetLevel(string id, int level)
    {
        DimLevel.Validate(level);
        Invoke(() =>
        {
            if (unavailable && level != 0) throw new InvalidOperationException("Dimming disabled; restart required.");
            var overlay = overlays.SingleOrDefault(o => o.Display.Id == id)
                ?? throw new ArgumentException("Unknown display.", nameof(id));
            Apply(overlay, level);
        });
    }

    public void RestoreAll() => Invoke(Restore);

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void Apply(Overlay overlay, int level)
    {
        // No redraw, z-order churn or native call for unchanged levels.
        if (Volatile.Read(ref overlay.Level) == level) return;
        if (level == 0) ShowWindow(overlay.Window, 0);
        else
        {
            Check(SetLayeredWindowAttributes(overlay.Window, 0, DimLevel.Alpha(level), 2));
            if (overlay.Level == 0)
                Check(SetWindowPos(overlay.Window, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040));
        }
        Volatile.Write(ref overlay.Level, level);
    }

    private void Restore()
    {
        foreach (var overlay in overlays) Apply(overlay, 0);
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        // Never let a managed exception cross the unmanaged callback boundary.
        if (message == 0x0312 && wParam == 1) restoreRequested = true; // WM_HOTKEY
        if (message is 0x007e or 0x0218) topologyChanged = true; // display/power changes
        if (message == 0x0021) return 3; // MA_NOACTIVATE
        if (message == 0x0084 && window != controller) return -1; // HTTRANSPARENT
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void Run()
    {
        var instance = GetModuleHandleW(null);
        var registered = false;
        try
        {
            if (SetThreadDpiAwarenessContext(-4) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var wc = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure,
                Instance = instance, Background = GetStockObject(4), ClassName = className
            };
            if (RegisterClassExW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            // Hidden top-level window receives display broadcasts; a message-only window would not.
            controller = CreateWindowExW(0x80, className, "Shade controller", 0x80000000, 0, 0, 0, 0, 0, 0, instance, 0);
            if (controller == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var displays = Enumerate();
            if (displays.Count == 0) throw new InvalidOperationException("No active displays.");
            foreach (var display in displays)
            {
                var window = CreateWindowExW(OverlayStyle, className, "Shade overlay", 0x80000000,
                    display.X, display.Y, display.Width, display.Height, 0, 0, instance, 0);
                if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                overlays.Add(new(display, window));
                Check(SetLayeredWindowAttributes(window, 0, 0, 2));
            }
            Displays = displays.AsReadOnly();
            unavailable = !RegisterHotKey(controller, 1, 0x4000 | 1 | 2 | 4, 0x52);
            status = unavailable ? "Recovery hotkey unavailable. Dimming disabled; release Ctrl+Alt+Shift+R and restart."
                : "Recovery: Ctrl+Alt+Shift+R restores every display.";
            ready.SetResult();

            // Blocking OS wait: no timer, polling, render loop or continuously uploaded bitmap.
            while (!stopping)
            {
                var result = GetMessageW(out var message, 0, 0, 0);
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (result == 0) break;
                DispatchMessageW(ref message);
                if (topologyChanged)
                {
                    topologyChanged = false;
                    unavailable = true;
                    Restore();
                    status = "Display or power state changed. All displays restored; restart Shade to resume.";
                }
                if (restoreRequested) { restoreRequested = false; Restore(); }
                if (message.Id == WorkMessage) DrainCommands();
            }
        }
        catch (Exception ex)
        {
            lock (gate) failure = ex;
            status = "Overlay backend stopped. Close and restart Shade.";
            ready.TrySetException(ex);
        }
        finally
        {
            lock (gate)
            {
                disposed = true;
                while (commands.TryDequeue(out var work))
                    work.Completion.TrySetException(failure ?? new ObjectDisposedException(nameof(WindowsDimmingBackend)));
            }
            foreach (var overlay in overlays)
            {
                DestroyWindow(overlay.Window);
                Volatile.Write(ref overlay.Level, 0);
            }
            if (controller != 0) { UnregisterHotKey(controller, 1); DestroyWindow(controller); }
            if (registered) UnregisterClassW(className, instance);
            GC.KeepAlive(procedure);
        }
    }

    private static List<Display> Enumerate()
    {
        List<Display> result = [];
        var error = 0;
        MonitorCallback callback = (nint monitor, nint dc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfoW(monitor, ref info)) { error = Marshal.GetLastWin32Error(); return false; }
            var bounds = info.Monitor;
            result.Add(new(info.Device, $"Display {result.Count + 1}" + ((info.Flags & 1) != 0 ? " (primary)" : ""),
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
            return true;
        };
        var success = EnumDisplayMonitors(0, 0, callback, 0);
        if (error != 0) throw new Win32Exception(error);
        Check(success);
        return result;
    }

    private void Invoke(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failure is not null) throw new InvalidOperationException("Overlay backend failed.", failure);
            Check(PostMessageW(controller, WorkMessage, 0, 0));
            commands.Enqueue((action, completion));
        }
        completion.Task.GetAwaiter().GetResult();
    }

    private void DrainCommands()
    {
        while (true)
        {
            (Action Action, TaskCompletionSource Completion) work;
            lock (gate) { if (!commands.TryDequeue(out work)) return; }
            try { work.Action(); work.Completion.SetResult(); }
            catch (Exception ex)
            {
                if (ex is Win32Exception)
                {
                    Restore(); unavailable = true;
                    status = "Native overlay update failed. Displays restored; restart Shade.";
                }
                work.Completion.SetException(ex);
            }
        }
    }

    // Opt-in integration test examines owned windows without exposing monitor IDs or taking desktop captures.
    public void VerifyNativeState() => Invoke(() =>
    {
        foreach (var o in overlays)
        {
            Check(GetWindowRect(o.Window, out var rect));
            Check(GetLayeredWindowAttributes(o.Window, out _, out var alpha, out _));
            var d = o.Display;
            if (rect.Left != d.X || rect.Top != d.Y || rect.Right - rect.Left != d.Width || rect.Bottom - rect.Top != d.Height)
                throw new InvalidOperationException("Overlay bounds mismatch.");
            if (((uint)GetWindowLongPtrW(o.Window, -20) & OverlayStyle) != OverlayStyle)
                throw new InvalidOperationException("Overlay styles mismatch.");
            if (IsWindowVisible(o.Window) != (o.Level != 0) || (o.Level != 0 && alpha != DimLevel.Alpha(o.Level)))
                throw new InvalidOperationException("Overlay visibility or alpha mismatch.");
        }
    });

    public void Dispose()
    {
        lock (gate) { if (disposed) return; }
        try { Invoke(() => stopping = true); }
        catch (ObjectDisposedException) { }
        finally { thread.Join(); }
    }
}
