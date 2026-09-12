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
        public Display Display { get; set; } = display;
        public nint Window { get; } = window;
        public int Level;
    }
    private readonly object gate = new();
    private readonly Queue<(Action Action, TaskCompletionSource Completion)> commands = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly WindowProcedure procedure;
    private readonly List<Overlay> overlays = [];
    private sealed record Snapshot(IReadOnlyList<Display> Displays, IReadOnlyDictionary<string, int> Levels, long Revision, long RestoreVersion);
    private volatile Snapshot snapshot = new(Array.Empty<Display>(), new Dictionary<string, int>(), 0, 0);
    private readonly SettingsStore? store;
    private readonly ShadeSettings settings;
    private readonly Func<ISet<string>, IReadOnlyList<Display>> catalog;
    private readonly uint recoveryKey;
    private readonly string className = "Shade.Overlay." + Guid.NewGuid().ToString("N");
    private nint controller;
    private bool stopping, unavailable, restoreRequested, topologyChanged;
    private bool hotkeyAvailable, settingsDirty;
    private bool disposed;
    private Exception? failure;
    private volatile string status = "Starting overlays...";
    public IReadOnlyList<Display> Displays => snapshot.Displays;
    public long Revision => snapshot.Revision;
    private long restoreVersion;
    public long RestoreVersion => snapshot.RestoreVersion;
    public string Status => store?.Error ?? status;
    public bool SettingsNeedRecovery => store?.Error is not null;
    public bool SettingsNeedBackup => store?.PreservingUnreadableFile == true;
    public void RecoverSettings() => Invoke(() =>
    {
        if (store is not null && store.Recover(settings)) { settingsDirty = false; KillTimer(controller, 2); }
        Publish();
    });
    private volatile ControlPreferences? preferences;
    public ControlPreferences? Preferences => preferences;
    private IReadOnlyList<Display> rawDisplays = Array.Empty<Display>();
    private volatile AssignmentChoice[] assignmentChoices = [];
    public bool SupportsAssignments => true;
    public IReadOnlyList<AssignmentChoice> Assignments => Array.AsReadOnly(assignmentChoices);
    public void AssignScreen(string connectionId, string name, string? existingId = null) => Invoke(() =>
    {
        // Read fresh topology before trusting a UI selection that may have been on screen for a while.
        var current = catalog(settings.AmbiguousHardware);
        IdentityAssignments.Assign(settings, current, connectionId, name, existingId);
        PublishPreferences();
        ScheduleSave();
        Reconcile();
    });
    public void DetachAssignment(string id) => Invoke(() =>
    {
        IdentityAssignments.Detach(settings, id);
        PublishPreferences();
        ScheduleSave();
        Reconcile();
    });
    public void SavePreferences(ControlPreferences value) => Invoke(() =>
    {
        settings.GlobalLevel = DimLevel.Validate(value.GlobalLevel);
        foreach (var display in overlays.Select(o => o.Display).Where(d => d.CanRemember))
            if (value.Screens.TryGetValue(display.Id, out var control))
            {
                DimLevel.Validate(control.IndividualLevel);
                settings.Controls[display.Id] = control;
            }
        settings.SetFullShade(value.AllowFullShade);
        PublishPreferences();
        ScheduleSave();
    });

    private void PublishPreferences() => preferences = new(settings.GlobalLevel,
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, RememberedControl>(new Dictionary<string, RememberedControl>(settings.Controls)), settings.AllowFullShade);

    public WindowsDimmingBackend(string? settingsPath = null) : this(settingsPath, WindowsDisplayCatalog.Read) { }

    internal WindowsDimmingBackend(string? settingsPath, Func<ISet<string>, IReadOnlyList<Display>> catalog, uint recoveryKey = 0x52)
    {
        this.catalog = catalog;
        this.recoveryKey = recoveryKey;
        store = settingsPath is null ? null : new SettingsStore(settingsPath);
        settings = store?.Load() ?? new();
        PublishPreferences();
        procedure = WindowProc;
        thread = new Thread(Run) { IsBackground = true, Name = "Shade overlays" };
        thread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { thread.Join(); throw; }
    }

    public int GetLevel(string id)
    {
        // A stale UI frame can refer to a just-disconnected display; report it undimmed.
        return snapshot.Levels.GetValueOrDefault(id);
    }

    public void SetLevel(string id, int level)
    {
        DimLevel.Validate(level);
        Invoke(() =>
        {
            if (unavailable && level != 0) throw new InvalidOperationException("Dimming is temporarily unavailable.");
            var overlay = overlays.SingleOrDefault(o => o.Display.Id == id)
                ?? throw new ArgumentException("Unknown display.", nameof(id));
            if (overlay.Level == level) return;
            Apply(overlay, level);
            Remember(overlay);
            Publish();
        });
    }

    public void RestoreAll() => Invoke(RestoreAndForgetLevels);

    public void SetLevels(IReadOnlyDictionary<string, int> levels)
    {
        var changes = levels.Select(p => (p.Key, Level: DimLevel.Validate(p.Value))).ToArray();
        if (changes.Length == 0) return;
        Invoke(() =>
        {
            if (unavailable && changes.Any(p => p.Level != 0)) throw new InvalidOperationException("Dimming is temporarily unavailable.");
            // Resolve every target before changing any surface; hotplug may invalidate a UI frame.
            var targets = changes.Select(p => (Overlay: overlays.SingleOrDefault(o => o.Display.Id == p.Key)
                ?? throw new ArgumentException("Unknown display."), p.Level)).ToArray();
            var changed = false;
            foreach (var target in targets)
            {
                if (target.Overlay.Level == target.Level) continue;
                Apply(target.Overlay, target.Level); Remember(target.Overlay); changed = true;
            }
            if (changed) Publish();
        });
    }

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
        // Hide even if a previous native operation failed before updating the tracked level.
        foreach (var overlay in overlays) { ShowWindow(overlay.Window, 0); Volatile.Write(ref overlay.Level, 0); }
    }

    private void RestoreAndForgetLevels()
    {
        Restore();
        Interlocked.Increment(ref restoreVersion);
        foreach (var key in settings.Displays.Keys.ToArray()) settings.Displays[key] = settings.Displays[key] with { Level = 0 };
        foreach (var key in settings.Controls.Keys.ToArray()) settings.Controls[key] = settings.Controls[key] with { Enabled = false };
        PublishPreferences();
        ScheduleSave();
        Publish();
    }

    private void Remember(Overlay overlay)
    {
        var d = overlay.Display;
        if (!d.CanRemember) return;
        settings.Displays[d.Id] = new(overlay.Level, d.X, d.Y, d.Width, d.Height);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        settingsDirty = true;
        if (store is not null && SetTimer(controller, 2, 750, 0) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void Save()
    {
        KillTimer(controller, 2);
        if (settingsDirty && store is not null) { store.Save(settings); settingsDirty = false; Publish(); }
    }

    private void Publish()
    {
        assignmentChoices = settings.Assignments.Select(p => new AssignmentChoice(p.Key, p.Value.Name,
            rawDisplays.Any(d => d.Id == p.Value.ConnectionId))).ToArray();
        snapshot = new(Array.AsReadOnly(overlays.Select(o => o.Display).ToArray()),
            overlays.ToDictionary(o => o.Display.Id, o => o.Level), snapshot.Revision + 1, restoreVersion);
    }

    private void Reconcile()
    {
        try
        {
            rawDisplays = catalog(settings.AmbiguousHardware);
            var displays = rawDisplays.Select(d => IdentityAssignments.Resolve(d, settings.Assignments)).ToArray();
            var ids = displays.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var old in overlays.Where(o => !ids.Contains(o.Display.Id)).ToArray())
            {
                Check(DestroyWindow(old.Window));
                overlays.Remove(old);
            }
            foreach (var display in displays)
            {
                var overlay = overlays.SingleOrDefault(o => o.Display.Id == display.Id);
                var desired = 0;
                if (overlay is null)
                {
                    var window = CreateWindowExW(OverlayStyle, className, "Shade overlay", 0x80000000,
                        display.X, display.Y, display.Width, display.Height, 0, 0, GetModuleHandleW(null), 0);
                    if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    overlay = new(display, window);
                    overlays.Add(overlay);
                    Check(SetLayeredWindowAttributes(window, 0, 0, 2));
                    if (display.CanRemember) desired = RememberedLevel(display.Id);
                }
                else
                {
                    desired = overlay.Level;
                    if (unavailable && display.CanRemember) desired = RememberedLevel(display.Id);
                    if (overlay.Display != display)
                    {
                        Check(SetWindowPos(overlay.Window, 0, display.X, display.Y, display.Width, display.Height, 0x0004 | 0x0010));
                        overlay.Display = display;
                    }
                }
                if (hotkeyAvailable) Apply(overlay, desired);
                // Record geometry separately from identity. Never change the OS display configuration.
                if (display.CanRemember && hotkeyAvailable) Remember(overlay);
            }
            unavailable = !hotkeyAvailable;
            KillTimer(controller, 1);
            status = hotkeyAvailable ? "Recovery: Ctrl+Alt+Shift+R restores every display. Display changes are automatic."
                : "Recovery hotkey unavailable. Dimming disabled; release Ctrl+Alt+Shift+R and restart.";
            ScheduleSave(); // Also preserves the duplicate-serial ledger.
            Publish();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Restore();
            unavailable = true;
            status = "Display configuration is changing or unavailable. Shading paused; retrying automatically.";
            // Only transient failure arms a retry timer; stable topology has no polling.
            if (SetTimer(controller, 1, 1000, 0) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Publish();
        }
    }

    private int RememberedLevel(string id) => settings.Controls.TryGetValue(id, out var control)
        ? control.EffectiveLevel(settings.GlobalLevel) : settings.Displays.GetValueOrDefault(id)?.Level ?? 0;

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        // Never let a managed exception cross the unmanaged callback boundary.
        if (message == 0x0312 && wParam == 1) restoreRequested = true; // WM_HOTKEY
        if (window == controller && message is 0x007e or 0x0218 or 0x0219) topologyChanged = true; // display/power/device changes
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
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = procedure,
                Instance = instance,
                Background = GetStockObject(4),
                ClassName = className
            };
            if (RegisterClassExW(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            // Hidden top-level window receives display broadcasts; a message-only window would not.
            controller = CreateWindowExW(0x80, className, "Shade controller", 0x80000000, 0, 0, 0, 0, 0, 0, instance, 0);
            if (controller == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            hotkeyAvailable = RegisterHotKey(controller, 1, 0x4000 | 1 | 2 | 4, recoveryKey);
            Reconcile();
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
                    Reconcile();
                }
                if (restoreRequested) { restoreRequested = false; RestoreAndForgetLevels(); }
                if (message.Id == 0x0113 && message.WParam == 1) Reconcile();
                if (message.Id == 0x0113 && message.WParam == 2) Save();
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
            Save();
            Publish();
            if (controller != 0) { UnregisterHotKey(controller, 1); DestroyWindow(controller); }
            if (registered) UnregisterClassW(className, instance);
            GC.KeepAlive(procedure);
        }
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
                    status = "Native overlay update failed. Displays restored; retrying automatically.";
                    SetTimer(controller, 1, 1000, 0);
                    Publish();
                }
                work.Completion.SetException(ex);
            }
        }
    }

    // Opt-in integration test examines owned windows without exposing monitor IDs or taking desktop captures.
    internal void VerifyNativeState() => Invoke(() =>
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

    internal void RefreshTopology()
    {
        Check(PostMessageW(controller, 0x007e, 0, 0));
        Invoke(() => { }); // Queue barrier after WM_DISPLAYCHANGE processing.
    }
    internal void TriggerRecovery()
    {
        Check(PostMessageW(controller, 0x0312, 1, 0));
        Invoke(() => { }); // Exercises the same message handler used by the registered recovery key.
    }

    public void Dispose()
    {
        try
        {
            bool alreadyStopped;
            lock (gate) alreadyStopped = disposed || failure is not null;
            if (!alreadyStopped) Invoke(() => stopping = true);
        }
        catch (ObjectDisposedException) { }
        finally { thread.Join(); }
    }
}
