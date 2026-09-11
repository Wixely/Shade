using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MQTTnet.Protocol;
using MQTTnet.Server;

// External fixture for the PowerShell UI test. It never becomes an application dependency.
internal static class UiBrokerFixture
{
    public static async Task Run(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The live UI broker fixture requires Windows.");
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build());
        var tests = 0; var connections = 0;
        var expectedConfigurations = 1 + 3 * Shade.WindowsDisplayCatalog.Read(new HashSet<string>()).Count(d => d.CanRemember);
        var configurations = new ConcurrentDictionary<string, bool>();
        broker.ValidatingConnectionAsync += e =>
        {
            if (e.UserName != "synthetic-user" || e.Password != "synthetic-password")
                e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            else if (e.ClientId.EndsWith("_test", StringComparison.Ordinal)) Interlocked.Increment(ref tests);
            else Interlocked.Increment(ref connections);
            return Task.CompletedTask;
        };
        broker.InterceptingPublishAsync += e =>
        {
            if (e.ApplicationMessage.Topic.StartsWith("homeassistant/", StringComparison.Ordinal))
                configurations[e.ApplicationMessage.Topic] = e.ApplicationMessage.Payload.Length != 0;
            return Task.CompletedTask;
        };
        await broker.StartAsync();
        try
        {
            var lifetime = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = Path.Combine(directory, "broker.json");
            var consecutiveSharingFailures = 0;
            while (!File.Exists(Path.Combine(directory, "stop")) && lifetime.Elapsed < TimeSpan.FromMinutes(5))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Port = port, TestConnections = Volatile.Read(ref tests), Connections = Volatile.Read(ref connections),
                    ExpectedConfigurations = expectedConfigurations,
                    ActiveConfigurations = configurations.Values.Count(v => v), RemovedConfigurations = configurations.Values.Count(v => !v)
                });
                await File.WriteAllBytesAsync(snapshot + ".tmp", bytes);
                try { File.Move(snapshot + ".tmp", snapshot, true); consecutiveSharingFailures = 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // PowerShell's short-lived reader may omit FileShare.Delete on Windows.
                    // Retain the last complete snapshot and retry; persistent failures still fail the fixture.
                    if (++consecutiveSharingFailures >= 10) throw;
                }
                await Task.Delay(100);
            }
        }
        finally { await broker.StopAsync(); }
    }
}
