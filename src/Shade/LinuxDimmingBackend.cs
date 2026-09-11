using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Shade;

// The UI never calls Xlib: its process-wide failure handlers belong to the owned worker.
[SupportedOSPlatform("linux")]
internal sealed class LinuxDimmingBackend : IDimmingBackend
{
    private readonly string? settingsPath;
    private readonly bool testKey;
    private readonly bool wayland;
    private readonly string? workerExecutable;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<X11Response>> pending = new();
    private readonly object pipeGate = new();
    private readonly Task supervisor;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? process;
    private X11State state = new([], [], new(30, new Dictionary<string, RememberedControl>()), [], 0, "Starting X11 overlays.", false, false);
    private long revision, sequence, restoreOffset;
    private int disposed;

    public LinuxDimmingBackend(string? settingsPath, bool testKey = false, string? workerExecutable = null)
    {
        this.settingsPath = settingsPath; this.testKey = testKey;
        this.workerExecutable = workerExecutable;
        wayland = !testKey && LinuxDesktopSession.Current().UsesWayland;
        if (wayland) state = state with { Status = "Starting Wayland overlays." };
        supervisor = Task.Run(Supervise);
        // A failed connection still opens the control UI with an actionable status.
        ready.Task.Wait(TimeSpan.FromSeconds(10));
    }
    private X11State State => Volatile.Read(ref state);
    public IReadOnlyList<Display> Displays => State.Displays;
    public string Status => State.Status;
    public long Revision => Interlocked.Read(ref revision);
    public long RestoreVersion => State.RestoreVersion;
    public ControlPreferences Preferences => State.Preferences;
    public bool SupportsAssignments => true;
    public IReadOnlyList<AssignmentChoice> Assignments => State.Assignments;
    public bool SettingsNeedRecovery => State.NeedsRecovery;
    public bool SettingsNeedBackup => State.NeedsBackup;
    public bool RecoverySetupNeeded => State.RecoverySetupNeeded;
    public void ConfigureRecovery() => Send(new(0, "configure-recovery"));
    public int GetLevel(string id) => State.Levels.GetValueOrDefault(id);
    public void SetLevel(string id, int level) => Send(new(0, "set", id, DimLevel.Validate(level)));
    public void SetLevels(IReadOnlyDictionary<string, int> levels)
    {
        if (levels.Count != 0) Send(new(0, "set-many", Levels: levels.ToDictionary(p => p.Key, p => DimLevel.Validate(p.Value))));
    }
    public void SavePreferences(ControlPreferences preferences) => Send(new(0, "preferences", Preferences: preferences));
    public void RestoreAll() => Send(new(0, "restore"));
    public void AssignScreen(string connectionId, string name, string? existingId = null)
        => Send(new(0, "assign", connectionId, Name: name, ExistingId: existingId));
    public void DetachAssignment(string id) => Send(new(0, "detach", id));
    public void RecoverSettings() => Send(new(0, "recover"));
    internal void VerifyNative() => Send(new(0, "verify"));
    internal void Refresh() => Send(new(0, "refresh"));
    internal int WorkerId { get { lock (pipeGate) return process?.Id ?? 0; } }

    private void Send(X11Request request)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var completion = new TaskCompletionSource<X11Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref sequence);
        pending[id] = completion;
        Process? target = null;
        try
        {
            lock (pipeGate)
            {
                target = process;
                if (target is null || target.HasExited) throw new InvalidOperationException("Linux overlays are reconnecting.");
                target.StandardInput.WriteLine(JsonSerializer.Serialize(request with { Sequence = id }, ShadeJsonContext.Default.X11Request));
                target.StandardInput.Flush();
            }
            var response = completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (response.Error is not null)
            {
                if (request.Operation is "assign" or "detach") throw new AssignmentException(response.Error);
                throw new InvalidOperationException(response.Error);
            }
        }
        catch (TimeoutException)
        {
            // A hung worker must not leave unresponsive dark overlays on the desktop.
            Stop(target);
            throw new InvalidOperationException("Linux overlays stopped responding and are being restarted.");
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task Supervise()
    {
        var retrySeconds = 1;
        while (!lifetime.IsCancellationRequested)
        {
            Process? child = null;
            try
            {
                var executable = workerExecutable ?? Environment.ProcessPath ?? throw new InvalidOperationException();
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                if (workerExecutable is null && Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
                start.ArgumentList.Add(wayland ? "--wayland-worker" : "--x11-worker");
                start.ArgumentList.Add(settingsPath ?? "");
                if (testKey) start.ArgumentList.Add("--test-key");
                child = Process.Start(start) ?? throw new InvalidOperationException();
                // Drain, but do not expose Xlib diagnostics containing local display details.
                var errors = DrainErrors(child);
                var first = true;
                while (await child.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
                {
                    if (line.Length > 2 * 1024 * 1024) throw new InvalidDataException();
                    var response = JsonSerializer.Deserialize(line, ShadeJsonContext.Default.X11Response) ?? throw new InvalidDataException();
                    Volatile.Write(ref state, response.State with { RestoreVersion = restoreOffset + response.State.RestoreVersion });
                    Interlocked.Increment(ref revision);
                    if (first)
                    {
                        lock (pipeGate) { if (!lifetime.IsCancellationRequested) process = child; }
                        first = false; retrySeconds = 1; ready.TrySetResult();
                    }
                    if (response.Sequence != 0 && pending.TryGetValue(response.Sequence, out var completion)) completion.TrySetResult(response);
                }
                Stop(child);
                await errors;
            }
            catch (Exception) { Stop(child); }
            finally
            {
                lock (pipeGate) { if (process == child) process = null; }
                child?.Dispose();
                foreach (var completion in pending.Values) completion.TrySetException(new InvalidOperationException("Linux overlay connection closed."));
                var previous = State;
                // Reconnection is not emergency restore: retain enabled-at-zero and exclusion intent.
                // The next worker starts its recovery counter at zero, so carry the existing epoch forward.
                restoreOffset = previous.RestoreVersion;
                Volatile.Write(ref state, previous with { Displays = [], Levels = [], RestoreVersion = restoreOffset,
                    RecoverySetupNeeded = false,
                    Status = wayland ? "Wayland worker unavailable; reconnecting automatically."
                        : "X11 overlays unavailable; reconnecting automatically. Check the X11 session and compositor." });
                Interlocked.Increment(ref revision); ready.TrySetResult();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), lifetime.Token); }
            catch (OperationCanceledException) { break; }
            retrySeconds = Math.Min(30, retrySeconds * 2);
        }
    }
    private static async Task DrainErrors(Process child)
    {
        var buffer = new char[1024];
        while (await child.StandardError.ReadAsync(buffer) != 0) { }
    }
    private static void Stop(Process? child)
    {
        try { if (child is not null && !child.HasExited) { child.Kill(); child.WaitForExit(3000); } }
        catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (pipeGate)
        {
            if (process is { } child)
            {
                try { child.StandardInput.Close(); if (!child.WaitForExit(3000)) Stop(child); }
                catch (IOException) { Stop(child); }
            }
        }
        lifetime.Cancel();
        supervisor.GetAwaiter().GetResult();
        lifetime.Dispose();
    }
}
