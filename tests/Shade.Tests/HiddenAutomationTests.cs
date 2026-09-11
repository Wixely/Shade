using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CupriFace.Shell;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Shade;

[SupportedOSPlatform("windows")]
internal static class HiddenAutomationTests
{
    public static void Run()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build());
        broker.StartAsync().GetAwaiter().GetResult();
        using var integration = new HomeAssistantIntegration();
        using var backend = new WindowsDimmingBackend(null, _ => [new("hidden-a", "Synthetic A", 10, 10, 80, 80, true),
            new("hidden-b", "Synthetic B", 100, 10, 80, 80, true)], 0x7A);
        var received = new ConcurrentDictionary<string, string>();
        using var observer = new MqttClientFactory().CreateMqttClient();
        observer.ApplicationMessageReceivedAsync += e =>
        { received[e.ApplicationMessage.Topic] = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray()); return Task.CompletedTask; };
        observer.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithProtocolVersion(MqttProtocolVersion.V500).Build()).GetAwaiter().GetResult();
        observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("#").Build()).GetAwaiter().GetResult();
        integration.ConfigureAsync(new("127.0.0.1", port, false, "", ""), true, false).GetAwaiter().GetResult();
        var driver = Task.Run(async () =>
        {
            nint window = 0;
            try
            {
                await Wait(() => (window = FindWindow()) != 0, "Control window did not appear");
                await Wait(() => received.Keys.Any(k => k.StartsWith("shade/") && k.EndsWith("/screen/hidden-a/level/state")), "UI did not publish initial state");
                var root = received.Keys.First(k => k.StartsWith("shade/") && k.EndsWith("/screen/hidden-a/level/state")).Split('/');
                var prefix = root[0] + "/" + root[1];
                ShowWindow(window, 0);
                await Wait(() => !IsWindowVisible(window), "Window did not hide");
                async Task Command(string suffix, string payload) => await observer.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic(prefix + suffix).WithPayload(payload).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
                await Command("/global/set", "3");
                await Wait(() => received.GetValueOrDefault(prefix + "/global/state") == "3", "Hidden global command not processed");
                Check(backend.GetLevel("hidden-a") == 0 && backend.GetLevel("hidden-b") == 0, "Hidden global command enabled screens");
                await Command("/screen/hidden-a/enabled/set", "ON");
                await Wait(() => backend.GetLevel("hidden-a") == 3, "Hidden enable command not applied");
                backend.VerifyNativeState();
                await Command("/screen/hidden-b/level/set", "4");
                await Wait(() => backend.GetLevel("hidden-b") == 4, "Hidden individual command not applied");
                await Command("/global/set", "5");
                await Wait(() => backend.GetLevel("hidden-a") == 5 && received.GetValueOrDefault(prefix + "/screen/hidden-a/level/state") == "5", "Hidden state not republished");
                Check(backend.GetLevel("hidden-b") == 4, "Hidden global command changed independent screen");
                backend.TriggerRecovery();
                await Wait(() => received.GetValueOrDefault(prefix + "/screen/hidden-a/enabled/state") == "OFF"
                    && received.GetValueOrDefault(prefix + "/screen/hidden-b/enabled/state") == "OFF", "Hidden recovery not published");
                await Command("/global/set", "6");
                await Wait(() => received.GetValueOrDefault(prefix + "/global/state") == "6", "Hidden recovery command barrier missing");
                Check(backend.GetLevel("hidden-a") == 0 && backend.GetLevel("hidden-b") == 0, "Global revived recovered screens");
                backend.VerifyNativeState();
                Check(!IsWindowVisible(window), "Automation unexpectedly revealed the control window");
                ShowWindow(window, 4);
                await Wait(() => IsWindowVisible(window), "Window did not reopen");
                // Exit with a real overlay still active to check disposal after DesktopHost.Run.
                await Command("/screen/hidden-a/enabled/set", "ON");
                await Wait(() => backend.GetLevel("hidden-a") == 6, "Final native overlay not enabled");
            }
            finally { if (window != 0) PostMessageW(window, 0x0010, 0, 0); }
        });
        try
        {
            // Same native hiding operation as close-to-tray; ordinary close permits deterministic
            // host shutdown here. Separate live tray tests verify the actual tray callbacks/menu.
            DesktopHost.Run(new ShadeApp(backend, integration, closeToTray: false));
            driver.GetAwaiter().GetResult();
            var windows = OwnedOverlayWindows();
            Check(windows.Count > 0, "Native overlay cleanup oracle had no windows");
            backend.Dispose();
            Check(windows.All(w => !IsWindow(w)), "Native overlay survived host shutdown and backend disposal");
        }
        finally { backend.Dispose(); integration.Dispose(); broker.StopAsync().GetAwaiter().GetResult(); }
    }
    private static async Task Wait(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline) { if (condition()) return; await Task.Delay(20); }
        throw new Exception(message);
    }
    private static nint FindWindow()
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var process);
            if (process != Environment.ProcessId) return true;
            var text = new StringBuilder(128); GetWindowTextW(window, text, text.Capacity);
            if (text.ToString().StartsWith("Shade - feasibility")) { found = window; return false; }
            return true;
        }, 0);
        return found;
    }
    private static List<nint> OwnedOverlayWindows()
    {
        List<nint> result = [];
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var process);
            if (process != Environment.ProcessId) return true;
            var name = new StringBuilder(128); GetClassNameW(window, name, name.Capacity);
            if (name.ToString().StartsWith("Shade.Overlay.")) result.Add(window);
            return true;
        }, 0);
        return result;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private delegate bool Callback(nint window, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, nint state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}
