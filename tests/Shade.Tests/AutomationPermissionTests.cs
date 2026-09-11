using System.Net;
using System.Net.Sockets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Shade;

internal static class AutomationPermissionTests
{
    public static async Task Run()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build());
        var denySubscribe = 1; var denyPublish = 0; var rejectedSubscriptions = 0; var rejectedPublications = 0;
        var rejectedPublishTimes = new System.Collections.Concurrent.ConcurrentQueue<long>();
        broker.InterceptingSubscriptionAsync += e =>
        {
            if (Volatile.Read(ref denySubscribe) != 0)
            {
                e.ProcessSubscription = false; e.Response.ReasonCode = MqttSubscribeReasonCode.NotAuthorized;
                Interlocked.Increment(ref rejectedSubscriptions);
            }
            return Task.CompletedTask;
        };
        broker.InterceptingPublishAsync += e =>
        {
            if (Volatile.Read(ref denyPublish) != 0 && e.ApplicationMessage.Topic.StartsWith("homeassistant/"))
            {
                e.ProcessPublish = false; e.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
                rejectedPublishTimes.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
                Interlocked.Increment(ref rejectedPublications);
            }
            return Task.CompletedTask;
        };
        await broker.StartAsync();
        try
        {
            using var integration = new HomeAssistantIntegration();
            using var backend = new FakeBackend();
            backend.ReplaceDisplays(FakeBackend.InitialDisplays.Select(d => d with { CanRemember = true }).ToArray());
            var model = new ShadeModel(backend, integration);
            _ = model.Revision;
            var connection = new BrokerConnection("127.0.0.1", port, false, "", "");
            await integration.ConfigureAsync(connection, true, false);
            await Wait(() => Volatile.Read(ref rejectedSubscriptions) > 0 && integration.Status.Contains("access denied"));
            Check(!integration.Status.StartsWith("Connected"), "Rejected subscriptions reported success");
            model.SetBindable("Level_a", 12); Check(backend.GetLevel("a") == 12, "Subscription failure blocked local control");
            Volatile.Write(ref denyPublish, 1); Volatile.Write(ref denySubscribe, 0);
            await Wait(() => Volatile.Read(ref rejectedPublications) >= 3 && integration.Status.Contains("access denied"));
            var times = rejectedPublishTimes.ToArray();
            Check(System.Diagnostics.Stopwatch.GetElapsedTime(times[1], times[2]) >= TimeSpan.FromSeconds(1.5),
                "Publication failures reset retry backoff after every subscription");
            Check(!integration.Status.StartsWith("Connected"), "Rejected discovery publication reported success");
            Volatile.Write(ref denyPublish, 0);
            await Wait(() => integration.Status.StartsWith("Connected"));
            Check(backend.GetLevel("a") == 12, "Permission recovery changed local control");
            await integration.DisableAsync(false);
        }
        finally { await broker.StopAsync(); }
    }
    private static async Task Wait(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(20, deadline.Token);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
