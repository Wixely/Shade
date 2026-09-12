using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Shade;

static class AutomationTests
{
    public static async Task Run()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var server = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build());
        var shadeConnections = 0;
        server.ValidatingConnectionAsync += e =>
        {
            if (e.UserName != "synthetic-user" || e.Password != "synthetic-password") e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            else if (e.ClientId.StartsWith("shade_") && !e.ClientId.EndsWith("_test")) Interlocked.Increment(ref shadeConnections);
            return Task.CompletedTask;
        };
        await server.StartAsync();
        var received = new ConcurrentDictionary<string, string>();
        using var observer = new MqttClientFactory().CreateMqttClient();
        observer.ApplicationMessageReceivedAsync += e =>
        { received[e.ApplicationMessage.Topic] = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray()); return Task.CompletedTask; };
        var observerOptions = new MqttClientOptionsBuilder().WithClientId("shade-test-observer").WithTcpServer("127.0.0.1", port)
            .WithCredentials("synthetic-user", "synthetic-password").WithProtocolVersion(MqttProtocolVersion.V500).Build();
        await observer.ConnectAsync(observerOptions);
        await observer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("#").Build());
        var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "automation.json");
        try
        {
            using var integration = new HomeAssistantIntegration(path);
            using var backend = new FakeBackend();
            backend.ReplaceDisplays([new("untrusted", "Unassigned screen", -800, 0, 800, 600),
                .. FakeBackend.InitialDisplays.Select(d => d with { CanRemember = true, Label = "Identical monitor" })]);
            var model = new ShadeModel(backend, integration);
            void Wait(Func<bool> condition, string failure)
            {
                var deadline = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < deadline) { _ = model.Revision; if (condition()) return; Thread.Sleep(10); }
                throw new Exception(failure + "; " + integration.Status);
            }
            var connection = new BrokerConnection("127.0.0.1", port, false, "synthetic-user", "synthetic-password");
            await integration.TestAsync(connection with { Password = "wrong" }, false);
            Check(integration.Status.Contains("failed"), "Invalid credentials passed test");
            await integration.TestAsync(connection, false);
            Check(integration.Status.Contains("succeeded"), "Valid connection failed");
            Check(!received.Keys.Any(k => k.StartsWith("homeassistant/")), "Connection test published discovery");
            await integration.ConfigureAsync(connection, true, false);
            var settings = new AutomationSettingsStore(path).Load();
            var root = "shade/" + settings.InstallationId;
            var a = root + "/screen/a"; var b = root + "/screen/b";
            Wait(() => received.GetValueOrDefault(root + "/availability") == "online" && received.ContainsKey(a + "/level/state"), "Discovery not published");
            foreach (var display in model.Displays.Where(d => d.Id is "a" or "b"))
            foreach (var (component, suffix) in new[] { ("switch", "enabled"), ("switch", "global"), ("number", "level") })
            {
                var topic = $"homeassistant/{component}/{settings.InstallationId}/{display.Id}_{suffix}/config";
                Wait(() => received.ContainsKey(topic), "Screen discovery missing");
                using var named = JsonDocument.Parse(received[topic]);
                Check(named.RootElement.GetProperty("name").GetString()!.StartsWith($"Screen {display.Number}: {display.Label} ", StringComparison.Ordinal),
                    "Home Assistant screen number differs from the monitor graphic");
                Check(named.RootElement.GetProperty("unique_id").GetString() == $"{settings.InstallationId}_{display.Id}_{suffix}", "Number changed entity identity");
            }
            Check(!File.ReadAllText(path).Contains("synthetic-password"), "Password stored in plaintext");
            Check(await AutomationSettingsStore.UnprotectAsync(settings.ProtectedPassword) == "synthetic-password", "Credential protection failed");
            using (var config = JsonDocument.Parse(received[$"homeassistant/number/{settings.InstallationId}/a_level/config"]))
            { Check(config.RootElement.GetProperty("max").GetInt32() == 80, "Wrong dimming scale"); Check(!config.RootElement.GetProperty("retain").GetBoolean(), "Discovery permits retained commands"); }
            async Task Command(string topic, string payload, bool retain = false) => await observer.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(topic).WithPayload(payload).WithRetainFlag(retain).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
            var globalConfig = $"homeassistant/number/{settings.InstallationId}/global/config";
            bool HasMaximum(int maximum) => received.TryGetValue(globalConfig, out var payload)
                && System.Text.Json.Nodes.JsonNode.Parse(payload)?["max"]?.GetValue<int>() == maximum;
            model.SetBindable(nameof(ShadeModel.AllowFullShade), true);
            Wait(() => HasMaximum(100), "100% limit was not published");
            await Command(root + "/global/set", "100");
            Wait(() => model.GlobalLevel == 100, "Full-shade MQTT command was rejected");
            Check(backend.GetLevel("a") == 0 && backend.GetLevel("b") == 0, "Full global enabled disabled screens");
            model.SetBindable(nameof(ShadeModel.AllowFullShade), false);
            Wait(() => HasMaximum(80) && model.GlobalLevel == 80, "Default limit did not return");
            await Command(root + "/global/set", "20.0");
            Wait(() => model.GlobalLevel == 20, "Global command not applied");
            Check(backend.GetLevel("a") == 0 && backend.GetLevel("b") == 0, "Global enabled screens");
            await Command(root + "/global/set", "20.5");
            await Command(a + "/enabled/set", "ON");
            Wait(() => backend.GetLevel("a") == 20 && received.GetValueOrDefault(a + "/enabled/state") == "ON", "Enable command/state missing");
            await Command(a + "/level/set", "15");
            Wait(() => backend.GetLevel("a") == 15 && received.GetValueOrDefault(a + "/global_link/state") == "OFF", "Individual command did not unlink");
            await Command(a + "/level/set", "70", true);
            await Command(root + "/global/set", "21");
            Wait(() => model.GlobalLevel == 21, "Command barrier missing");
            Check(backend.GetLevel("a") == 15, "Retained command applied");
            model.SetBindable("Level_b", 12);
            Wait(() => received.GetValueOrDefault(b + "/level/state") == "12", "Local UI state not published");
            backend.ReplaceDisplays([FakeBackend.InitialDisplays[0] with { CanRemember = true, X = -1920 }]);
            Wait(() => received.GetValueOrDefault(b + "/availability") == "offline", "Disconnected screen stayed available");
            Wait(() => received.GetValueOrDefault($"homeassistant/number/{settings.InstallationId}/a_level/config")?.Contains("Screen 1:") == true,
                "Display numbering did not update discovery after topology changed");
            var client = (await server.GetClientsAsync()).Single(c => c.Id == "shade_" + settings.InstallationId);
            await client.DisconnectAsync();
            Wait(() => Volatile.Read(ref shadeConnections) >= 2 && integration.Status.StartsWith("Connected"), "Reconnect failed");
            Check(backend.GetLevel("a") == 0, "Retained command replayed on reconnect");

            await server.StopAsync();
            received.Clear();
            model.SetBindable("Level_a", 12);
            Check(backend.GetLevel("a") == 12, "Broker outage prevented local control");
            await server.StartAsync();
            // A fresh observer avoids racing its old transport's asynchronous disconnect cleanup.
            // Shade's existing client must still reconnect autonomously, as checked by the connection count.
            using var restartedObserver = new MqttClientFactory().CreateMqttClient();
            restartedObserver.ApplicationMessageReceivedAsync += e =>
            { received[e.ApplicationMessage.Topic] = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray()); return Task.CompletedTask; };
            await restartedObserver.ConnectAsync(observerOptions);
            await restartedObserver.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("#").Build());
            Wait(() => Volatile.Read(ref shadeConnections) >= 3 && received.GetValueOrDefault(a + "/level/state") == "12"
                && received.GetValueOrDefault(root + "/availability") == "online", "Broker restart did not republish current state");

            integration.Dispose();
            using var resumed = new HomeAssistantIntegration(path);
            model = new ShadeModel(backend, resumed);
            received.Clear();
            await resumed.ResumeAsync();
            Wait(() => Volatile.Read(ref shadeConnections) >= 4 && received.GetValueOrDefault(root + "/availability") == "online"
                && received.GetValueOrDefault(a + "/level/state") == "12", "Saved connection did not resume with current state");
            Check(resumed.HasPassword, "Saved credential was lost on restart");
            await resumed.DisableAsync(true);
            Wait(() => received.GetValueOrDefault($"homeassistant/number/{settings.InstallationId}/a_level/config") == "", "Discovery removal missing");
            Check(!new AutomationSettingsStore(path).Load().Enabled, "Disable was not saved");
            await resumed.ForgetPasswordAsync();
            Check(new AutomationSettingsStore(path).Load().ProtectedPassword.Length == 0, "Forgotten password remained saved");
            await restartedObserver.DisconnectAsync();
        }
        finally
        {
            await server.StopAsync();
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
