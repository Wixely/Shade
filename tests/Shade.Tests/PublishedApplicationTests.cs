using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Shade;

static class PublishedApplicationTests
{
    public static async Task RunTls(string executable, int port, Func<int> completedConnections, Func<int> connectPackets, Func<int> applicationStreams)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The published TLS oracle requires Windows.");
        executable = Path.GetFullPath(executable);
        var display = WindowsDisplayCatalog.Read(new HashSet<string>()).FirstOrDefault(d => d.CanRemember)
            ?? throw new Exception("The published TLS check needs one trusted connected screen");
        var directory = Path.Combine(Path.GetTempPath(), "Shade.PublishedTests", Guid.NewGuid().ToString("N"));
        var evidence = Path.Combine("artifacts", "packaging", "published-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var settingsPath = Path.Combine(directory, "settings.json");
        var automationPath = Path.Combine(directory, "automation.json");
        Check(new SettingsStore(settingsPath).Save(new ShadeSettings
        {
            GlobalLevel = 2,
            Controls = new() { [display.Id] = new(true, false, 2) },
            Displays = new() { [display.Id] = new(2, display.X, display.Y, display.Width, display.Height) }
        }), "Could not create isolated TLS screen settings");
        Check(new AutomationSettingsStore(automationPath).Save(new AutomationSettings
        {
            Enabled = true, Tls = true, Host = "127.0.0.1", Port = port, Username = "synthetic-tls-user",
            ProtectedPassword = await AutomationSettingsStore.ProtectAsync("synthetic-tls-password")
        }), "Could not create isolated TLS integration settings");
        Process? child = null;
        Task<string>? stdout = null, stderr = null;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--no-tray"); start.ArgumentList.Add("--settings-directory"); start.ArgumentList.Add(directory);
            child = Process.Start(start) ?? throw new Exception("Published TLS application did not start");
            stdout = child.StandardOutput.ReadToEndAsync(); stderr = child.StandardError.ReadToEndAsync();
            var deadline = Stopwatch.StartNew();
            // One completed connection belongs to the pinned positive control. Require two
            // completed application attempts, rather than merely accepted TCP connections.
            while (completedConnections() < 3 || !VisibleAlphas(child.Id).SequenceEqual(new[] { DimLevel.Alpha(2) }))
            {
                Check(!child.HasExited, "Published TLS application exited unexpectedly");
                Check(deadline.Elapsed < TimeSpan.FromSeconds(20), "TLS retry or native settings restoration failed");
                await Task.Delay(25);
            }
            Check(connectPackets() == 1 && applicationStreams() == 1, "Published application sent application data through an untrusted TLS connection");
            var control = FindControlWindow(child.Id);
            Check(control != 0 && PostMessageW(control, 0x10, 0, 0), "Cannot close TLS application's control window");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await child.WaitForExitAsync(timeout.Token);
            Check(child.ExitCode == 0, "TLS application did not exit cleanly");
            Check(connectPackets() == 1 && applicationStreams() == 1, "Shutdown bypassed TLS validation");
            var errors = await stderr;
            Check(errors.Length == 0, "Published TLS application emitted an unexpected error; inspect private evidence");
            File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                Passed = true, ExecutableSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable))),
                Software = Environment.GetEnvironmentVariable("CUPRIFACE_SOFTWARE"),
                CompletedConnections = completedConnections(), MqttConnectPackets = connectPackets(), ApplicationStreams = applicationStreams()
            }));
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                File.WriteAllText(Path.Combine(evidence, "stdout.log"), await stdout!);
                File.WriteAllText(Path.Combine(evidence, "stderr.log"), await stderr!);
                child.Dispose();
            }
            foreach (var file in new[] { settingsPath, settingsPath + ".tmp", automationPath, automationPath + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
    public static async Task Run(string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This published oracle requires Windows.");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("Publish the application first.");
        var expectedScreens = WindowsDisplayCatalog.Read(new HashSet<string>()).Where(d => d.CanRemember).Select(d => d.Id).Order().ToArray();
        Check(expectedScreens.Length >= 2, "This check needs two trusted connected screens");
        // The application retains its ordinary session mutex and recovery key. An existing
        // instance makes this check fail, rather than being stopped or bypassed.
        var directory = Path.Combine(Path.GetTempPath(), "Shade.PublishedTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build());
        var denyConnection = 0; var denySubscribe = 0; var denyPublish = 0;
        var rejectedConnections = 0; var rejectedSubscriptions = 0; var rejectedPublications = 0;
        broker.ValidatingConnectionAsync += e =>
        {
            if (e.UserName != "synthetic-user" || e.Password != "synthetic-password")
                e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            else if (e.ClientId.StartsWith("shade_", StringComparison.Ordinal) && Volatile.Read(ref denyConnection) != 0)
            {
                e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
                Interlocked.Increment(ref rejectedConnections);
            }
            return Task.CompletedTask;
        };
        broker.InterceptingSubscriptionAsync += e =>
        {
            if (e.ClientId.StartsWith("shade_", StringComparison.Ordinal) && Volatile.Read(ref denySubscribe) != 0)
            {
                e.ProcessSubscription = false; e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
                Interlocked.Increment(ref rejectedSubscriptions);
            }
            return Task.CompletedTask;
        };
        broker.InterceptingPublishAsync += e =>
        {
            if (e.ClientId.StartsWith("shade_", StringComparison.Ordinal) && Volatile.Read(ref denyPublish) != 0 &&
                e.ApplicationMessage.Topic.StartsWith("homeassistant/", StringComparison.Ordinal))
            {
                e.ProcessPublish = false; e.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
                Interlocked.Increment(ref rejectedPublications);
            }
            return Task.CompletedTask;
        };
        await broker.StartAsync();
        using var observer = new MqttClientFactory().CreateMqttClient();
        var received = new ConcurrentDictionary<string, string>();
        observer.ApplicationMessageReceivedAsync += e =>
        { received[e.ApplicationMessage.Topic] = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray()); return Task.CompletedTask; };
        await observer.ConnectAsync(new MqttClientOptionsBuilder().WithClientId("published-oracle")
            .WithTcpServer("127.0.0.1", port).WithCredentials("synthetic-user", "synthetic-password")
            .WithProtocolVersion(MqttProtocolVersion.V500).Build());
        await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("#").Build());
        var automation = new AutomationSettings
        {
            Enabled = true, Host = "127.0.0.1", Port = port, Tls = false, Username = "synthetic-user",
            ProtectedPassword = await AutomationSettingsStore.ProtectAsync("synthetic-password")
        };
        var automationPath = Path.Combine(directory, "automation.json");
        var settingsPath = Path.Combine(directory, "settings.json");
        Check(new AutomationSettingsStore(automationPath).Save(automation), "Cannot seed isolated integration settings");
        Check(new SettingsStore(settingsPath).Save(new ShadeSettings { GlobalLevel = 2 }), "Cannot seed isolated screen settings");
        var root = "shade/" + automation.InstallationId;
        Process? child = null;
        Task<string>? stdout = null, stderr = null;
        var evidence = Path.Combine("artifacts", "packaging", "published-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        void Start()
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("--no-tray"); start.ArgumentList.Add("--settings-directory"); start.ArgumentList.Add(directory);
            child = Process.Start(start) ?? throw new Exception("Application did not start");
            stdout = child.StandardOutput.ReadToEndAsync(); stderr = child.StandardError.ReadToEndAsync();
        }
        async Task Wait(Func<bool> condition, string failure)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (child!.HasExited) throw new Exception($"Application exited with code {child.ExitCode}: {failure}");
                if (condition()) return;
                await Task.Delay(25);
            }
            throw new Exception(failure);
        }
        async Task Close(int run)
        {
            nint control = 0;
            await Wait(() =>
            {
                control = FindControlWindow(child!.Id);
                return control != 0;
            }, "No control window to close");
            Check(PostMessageW(control, 0x10, 0, 0), "Control window rejected close");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await child!.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new Exception($"Published run {run} did not exit after closing its control window"); }
            File.WriteAllText(Path.Combine(evidence, $"run-{run}.stdout.log"), await stdout!);
            var errors = await stderr!; File.WriteAllText(Path.Combine(evidence, $"run-{run}.stderr.log"), errors);
            Check(child.ExitCode == 0 && errors.Length == 0, "Published process failed on exit; inspect private evidence");
            child.Dispose(); child = null;
        }
        async Task Command(string topic, string value, bool retained = false) => await observer.PublishAsync(
            new MqttApplicationMessageBuilder().WithTopic(topic + "/set").WithPayload(value).WithRetainFlag(retained)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
        try
        {
            Start();
            await Wait(() => received.GetValueOrDefault(root + "/availability") == "online" &&
                received.GetValueOrDefault(root + "/global/state") == "2", "Saved credentials/global preference did not load");
            var screens = expectedScreens.Select(id => root + "/screen/" + id).ToArray();
            await Wait(() => screens.All(s => received.ContainsKey(s + "/attributes") && received.ContainsKey(s + "/enabled/state")),
                "Initial publication did not include every trusted connected screen");
            var first = screens[0]; var second = screens[1];
            var firstId = first[(root.Length + "/screen/".Length)..];
            var configTopic = $"homeassistant/number/{automation.InstallationId}/{firstId}_level/config";
            await Wait(() => received.ContainsKey(configTopic) && received.ContainsKey(first + "/attributes"), "Discovery or geometry missing");
            var geometry = received[first + "/attributes"];
            using (var config = JsonDocument.Parse(received[configTopic]))
            {
                var fields = config.RootElement;
                Check(fields.GetProperty("unique_id").GetString() == automation.InstallationId + "_" + firstId + "_level",
                    "Discovery identity differs from the screen identity");
                Check(fields.GetProperty("max").GetInt32() == 80 && !fields.GetProperty("retain").GetBoolean(), "Invalid discovery control contract");
            }
            await Command(root + "/global", "3");
            await Wait(() => received.GetValueOrDefault(root + "/global/state") == "3", "Global command failed");
            Check(screens.All(s => received.GetValueOrDefault(s + "/enabled/state") == "OFF"), "Global enabled a screen");
            await Command(first + "/enabled", "ON");
            await Wait(() => VisibleAlphas(child!.Id).SequenceEqual(new[] { DimLevel.Alpha(3) }), "Native shading did not follow MQTT enable");
            await Command(first + "/level", "2");
            await Wait(() => received.GetValueOrDefault(first + "/global_link/state") == "OFF", "Individual command did not unlink");
            await Command(first + "/level", "5", true);
            await Command(root + "/global", "4");
            await Wait(() => received.GetValueOrDefault(root + "/global/state") == "4", "Command barrier failed");
            Check(received.GetValueOrDefault(first + "/level/state") == "2", "Independent or retained-command isolation failed");
            Check(received.GetValueOrDefault(second + "/enabled/state") == "OFF", "Disabled screen changed");
            await Wait(() => VisibleAlphas(child!.Id).SequenceEqual(new[] { DimLevel.Alpha(2) }), "Native independent opacity changed");
            await Wait(() => new SettingsStore(settingsPath).Load() is var saved && saved.GlobalLevel == 4 &&
                saved.Controls.GetValueOrDefault(firstId) is { Enabled: true, UseGlobal: false, IndividualLevel: 2 }, "Application did not persist command intent");
            await Close(1);
            received.Clear(); // Require new publications; the observer remains connected, so no retained replay.
            Start();
            await Wait(() => received.GetValueOrDefault(root + "/global/state") == "4" &&
                received.GetValueOrDefault(first + "/enabled/state") == "ON" &&
                received.GetValueOrDefault(first + "/global_link/state") == "OFF" &&
                received.GetValueOrDefault(first + "/level/state") == "2" &&
                received.GetValueOrDefault(second + "/enabled/state") == "OFF", "Restart lost identity, credential or screen intent");
            await Wait(() => VisibleAlphas(child!.Id).SequenceEqual(new[] { DimLevel.Alpha(2) }), "Restart did not restore native opacity");
            await Wait(() => received.GetValueOrDefault(first + "/attributes") == geometry, "Restart changed the screen's layout association");
            // Exercise the packaged integration's failure/retry paths, keeping the observer authorized.
            Volatile.Write(ref denyConnection, 1);
            var connection = (await broker.GetClientsAsync()).Single(c => c.Id == "shade_" + automation.InstallationId);
            await connection.DisconnectAsync();
            await Wait(() => Volatile.Read(ref rejectedConnections) >= 2, "Application did not retry rejected credentials");
            Check(VisibleAlphas(child!.Id).SequenceEqual(new[] { DimLevel.Alpha(2) }), "Credential failure changed native shading");
            Volatile.Write(ref denySubscribe, 1); Volatile.Write(ref denyConnection, 0);
            await Wait(() => Volatile.Read(ref rejectedSubscriptions) > 0 &&
                received.GetValueOrDefault(root + "/availability") == "offline", "Subscription rejection did not leave integration offline");
            Check(VisibleAlphas(child!.Id).SequenceEqual(new[] { DimLevel.Alpha(2) }), "Subscription failure changed native shading");
            Volatile.Write(ref denyPublish, 1); Volatile.Write(ref denySubscribe, 0);
            await Wait(() => Volatile.Read(ref rejectedPublications) > 0, "Discovery publication rejection was not exercised");
            Check(VisibleAlphas(child!.Id).SequenceEqual(new[] { DimLevel.Alpha(2) }), "Publication failure changed native shading");
            received.Clear();
            Volatile.Write(ref denyPublish, 0);
            await Wait(() => received.GetValueOrDefault(root + "/availability") == "online" && received.ContainsKey(configTopic) &&
                received.GetValueOrDefault(first + "/level/state") == "2", "Restored permissions did not republish saved state");
            await Command(root + "/global", "6");
            await Wait(() => received.GetValueOrDefault(root + "/global/state") == "6", "Commands did not resume after permission recovery");
            Check(VisibleAlphas(child!.Id).SequenceEqual(new[] { DimLevel.Alpha(2) }), "Recovery changed independent shading or replayed retained command");
            await Command(first + "/enabled", "OFF");
            await Wait(() => VisibleAlphas(child!.Id).Count == 0 && received.GetValueOrDefault(first + "/enabled/state") == "OFF", "Restoration failed");
            await Close(2);
            Check(!File.ReadAllText(automationPath).Contains("synthetic-password"), "Plaintext credential saved");
            File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                Passed = true, ExecutableSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(executable))),
                Software = Environment.GetEnvironmentVariable("CUPRIFACE_SOFTWARE"), ConnectedScreens = screens.Length,
                RejectedConnections = rejectedConnections, RejectedSubscriptions = rejectedSubscriptions, RejectedPublications = rejectedPublications
            }));
            Console.WriteLine("Published oracle: commands, native opacity, persistence, credential/ACL rejection and automatic recovery passed.");
        }
        finally
        {
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
                File.WriteAllText(Path.Combine(evidence, "failure.stdout.log"), await stdout!);
                File.WriteAllText(Path.Combine(evidence, "failure.stderr.log"), await stderr!);
                child.Dispose();
            }
            await broker.StopAsync();
            // Only known files in this test's unique directory are removed; retain unexpected files.
            foreach (var file in new[] { settingsPath, settingsPath + ".tmp", automationPath, automationPath + ".tmp" })
                if (File.Exists(file)) File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static nint FindControlWindow(int process)
    {
        nint control = 0;
        EnumWindows((window, state) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            var title = new StringBuilder(128); GetWindowTextW(window, title, title.Capacity);
            if (owner == process && title.ToString() == "Shade") control = window;
            return true;
        }, 0);
        return control;
    }
    private static List<byte> VisibleAlphas(int process)
    {
        var result = new List<byte>();
        EnumWindows((window, state) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != process || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(64); GetWindowTextW(window, title, title.Capacity);
            if (title.ToString() == "Shade overlay")
            {
                Check(GetLayeredWindowAttributes(window, out _, out var alpha, out _), "Cannot read native opacity");
                result.Add(alpha);
            }
            return true;
        }, 0);
        return result;
    }
    private delegate bool WindowCallback(nint window, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint state);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
}
