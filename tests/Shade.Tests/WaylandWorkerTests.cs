using System.Diagnostics;
using System.Runtime.Versioning;
using Shade;
using Tmds.DBus.Protocol;

[SupportedOSPlatform("linux")]
internal static class WaylandWorkerTests
{
    internal static async Task Run(string? executable = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Shade.WorkerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var settings = Path.Combine(directory, "settings.json");
        var oldDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        var oldType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var start = new ProcessStartInfo("dbus-daemon") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--session"); start.ArgumentList.Add("--nofork"); start.ArgumentList.Add("--print-address=1");
        using var daemon = Process.Start(start) ?? throw new Exception("No isolated bus");
        await using var compositor = new WaylandCompositorFixture();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var token = timeout.Token;
            var address = await daemon.StandardOutput.ReadLineAsync(token) ?? throw new Exception("No bus address");
            using var service = new DBusConnection(address); await service.ConnectAsync(); await service.RequestNameAsync("org.freedesktop.portal.Desktop");
            var portal = new WaylandRecoveryTests.Portal(service); service.AddMethodHandler(portal);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", compositor.SocketPath);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", address);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");
            string assigned;
            using (var backend = new LinuxDimmingBackend(settings, workerExecutable: executable))
            {
                await Until(() => backend.Displays.Count == 2 && backend.RecoverySetupNeeded, "Initial worker catalog/setup state");
                Check(compositor.Mapped.Count == 0, "Startup mapped before recovery registration");
                var raw = backend.Displays.Single(d => d.X == 0).Id;
                backend.AssignScreen(raw, "Desk");
                assigned = backend.Displays.Single(d => d.CanRemember).Id;
                var controls = new ScreenControls(backend);
                controls.SetGlobal(40);
                Check(compositor.Mapped.Count == 0, "Global level enabled an output");
                backend.ConfigureRecovery();
                await Until(() => backend.Status.StartsWith("Wayland overlays active") && !backend.RecoverySetupNeeded, "Recovery registration did not complete");
                controls.Toggle(assigned);
                await Until(() => compositor.Mapped.GetValueOrDefault(20u) == 40, "Assigned screen did not map at 40%");
                await Task.Delay(1000, token); // allow the one-shot settings debounce to finish
                using (var process = Process.GetProcessById(backend.WorkerId))
                {
                    var cpu = process.TotalProcessorTime; var revision = backend.Revision; var requests = compositor.RequestCount;
                    await Task.Delay(2000, token); process.Refresh();
                    Check(backend.Revision == revision && compositor.RequestCount == requests, "Shaded idle worker published or wrote recurring protocol traffic");
                    Console.WriteLine($"  Shaded worker idle: {(process.TotalProcessorTime - cpu).TotalMilliseconds:F1} CPU ms / 2000 ms; zero publications and protocol requests.");
                }
                var portalSessions = Volatile.Read(ref portal.CreateCount);
                var workerId = backend.WorkerId;
                compositor.DisconnectClients();
                await Until(() => compositor.Mapped.Count == 0, "Compositor disconnect did not remove native surfaces");
                await Until(() => compositor.Mapped.GetValueOrDefault(20u) == 40 && backend.Displays.Any(d => d.Id == assigned), "Compositor reconnect did not restore assigned shading");
                Check(backend.WorkerId == workerId && !backend.RecoverySetupNeeded && Volatile.Read(ref portal.CreateCount) == portalSessions,
                    "Compositor reconnect restarted recovery authorization");
                controls.SetUseGlobal(assigned, false); controls.SetGlobal(55);
                await Until(() => backend.GetLevel(assigned) == 40, "Independent level changed with global");
                Check(compositor.Mapped.Count == 1, "Disabled output joined global shading");
                await compositor.Change(20, new("DP-1", -720, 40, 720, 1280));
                await Until(() => backend.Displays.Any(d => d.Id == assigned && d.Width == 720 && d.X == -720), "Rotation lost geometry or assignment");
                await compositor.Change(20, null);
                await Until(() => backend.Displays.Count == 1 && compositor.Mapped.Count == 0, "Unplug did not remove overlay");
                await compositor.Change(40, new("DP-1", 1920, 0, 1280, 720));
                await Until(() => backend.Displays.Any(d => d.Id == assigned) && compositor.Mapped.GetValueOrDefault(40u) == 40, "Replug did not recall the assigned independent screen");
                var restore = backend.RestoreVersion;
                portal.Activate(service, portal.Session, "restore-all");
                await Until(() => backend.RestoreVersion > restore && compositor.Mapped.Count == 0, "Recovery did not remove active shading");
                controls.SetGlobal(60); Check(compositor.Mapped.Count == 0, "Global revived a recovered screen");
                controls.Toggle(assigned);
                await Until(() => compositor.Mapped.GetValueOrDefault(40u) == 40, "Could not re-enable independent screen");
                await compositor.Change(40, null);
                await Until(() => backend.Displays.Count == 1 && compositor.Mapped.Count == 0, "Second unplug did not remove shading");
                restore = backend.RestoreVersion; portal.Activate(service, portal.Session, "restore-all");
                await Until(() => backend.RestoreVersion > restore, "Recovery while disconnected was not handled");
                await compositor.Change(40, new("DP-1", 1920, 0, 1280, 720));
                await Until(() => backend.Displays.Any(d => d.Id == assigned), "Disconnected assignment did not return");
                Check(!backend.Preferences.Screens[assigned].Enabled && backend.GetLevel(assigned) == 0 && compositor.Mapped.Count == 0,
                    "Returning screen undid emergency recovery");
                controls.Toggle(assigned);
                await Until(() => compositor.Mapped.GetValueOrDefault(40u) == 40, "Re-enable after detached recovery failed");
                portal.CloseSession();
                await Until(() => compositor.Mapped.Count == 0 && backend.RecoverySetupNeeded, "Recovery loss did not unmap and offer explicit setup");
                await Task.Delay(300, token);
                Check(compositor.Mapped.Count == 0, "Recovery loss automatically re-enabled shading");
            }
            using (var restart = new LinuxDimmingBackend(settings, workerExecutable: executable))
            {
                await Until(() => restart.Displays.Any(d => d.Id == assigned) && restart.RecoverySetupNeeded, "Assignment did not survive worker restart");
                Check(restart.Preferences.GlobalLevel == 60 && compositor.Mapped.Count == 0, "Restart lost global selection or bypassed recovery setup");
                restart.ConfigureRecovery();
                await Until(() => compositor.Mapped.GetValueOrDefault(40u) == 40, "Saved independent enabled intent did not return after registration");
                restart.RestoreAll(); await Until(() => compositor.Mapped.Count == 0, "Final restore failed");
            }
            compositor.CheckErrors();
            Console.WriteLine("Actual Wayland worker: setup, assigned persistence, independent global, rotation, replug, hotkey restore, recovery loss and restart passed against isolated protocol fixtures.");
            async Task Until(Func<bool> condition, string message)
            {
                var limit = Stopwatch.StartNew();
                while (!condition())
                {
                    compositor.CheckErrors();
                    if (limit.Elapsed > TimeSpan.FromSeconds(6)) throw new Exception(message);
                    await Task.Delay(10, token);
                }
                compositor.CheckErrors();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", oldDisplay);
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", oldBus);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", oldType);
            if (!daemon.HasExited) { daemon.Kill(); await daemon.WaitForExitAsync(); }
            if (File.Exists(settings)) File.Delete(settings);
            if (File.Exists(settings + ".tmp")) File.Delete(settings + ".tmp");
            Directory.Delete(directory);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
