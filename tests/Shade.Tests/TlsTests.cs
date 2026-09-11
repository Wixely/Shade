using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Formatter;
using Shade;

internal static class TlsTests
{
    public static async Task Run(string? publishedExecutable = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Reimport gives Schannel a certificate-backed key container for server authentication.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        await using var broker = new TlsProbeBroker(certificate);

        // A test-only pinned-certificate client proves the TLS endpoint and MQTT 5 CONNACK are usable.
        // It does not alter machine trust or inject any validation override into Shade.
        using (var control = new MqttClientFactory().CreateMqttClient())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", broker.Port)
                .WithProtocolVersion(MqttProtocolVersion.V500).WithTlsOptions(t => t.UseTls()
                    .WithCertificateValidationHandler(e => e.Certificate?.GetCertHashString() == certificate.GetCertHashString())).Build();
            var response = await control.ConnectAsync(options, deadline.Token);
            Check(response.ResultCode == MqttClientConnectResultCode.Success, "TLS test endpoint control failed");
            await control.DisconnectAsync(new MqttClientDisconnectOptions(), deadline.Token);
        }
        Check(broker.ConnectPackets == 1, "Control MQTT CONNECT was not observed");
        if (publishedExecutable is not null)
        {
            await PublishedApplicationTests.RunTls(publishedExecutable, broker.Port,
                () => broker.CompletedConnections, () => broker.ConnectPackets, () => broker.ApplicationStreams);
            return;
        }

        using var integration = new HomeAssistantIntegration();
        using var backend = new FakeBackend();
        backend.SetLevel("a", 15);
        var model = new ShadeModel(backend, integration);
        var connection = new BrokerConnection("127.0.0.1", broker.Port, true, "synthetic-tls-user", "");
        await integration.TestAsync(connection, false);
        Check(integration.Status.Contains("failed"), "Untrusted certificate passed connection test");
        Check(!integration.Enabled, "Connection test enabled the integration");
        Check(broker.ConnectPackets == 1, "Shade sent MQTT credentials through an untrusted TLS connection");

        var before = broker.Connections;
        await integration.ConfigureAsync(connection, true, false);
        var timeout = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < timeout && broker.Connections < before + 2)
        { _ = model.Revision; await Task.Delay(25); }
        Check(broker.Connections >= before + 2, "TLS failure was not retried");
        Check(broker.ConnectPackets == 1, "TLS reconnect bypassed certificate validation");
        Check(backend.GetLevel("a") == 15, "TLS failure changed local dimming");
        await integration.DisableAsync(false);
        Check(!integration.Enabled, "Could not disable failed TLS integration");
    }

    // Minimal protocol probe, bound only to loopback. Reads a complete MQTT CONNECT and sends
    // a successful MQTT 5 CONNACK; certificate rejection must happen before CONNECT is sent.
    internal sealed class TlsProbeBroker : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentBag<Task> clients = [];
        private readonly X509Certificate2 certificate;
        private readonly Task accept;
        private int connections, connectPackets, completedConnections, applicationStreams;
        public int Connections => Volatile.Read(ref connections);
        public int CompletedConnections => Volatile.Read(ref completedConnections);
        public int ApplicationStreams => Volatile.Read(ref applicationStreams);
        public int ConnectPackets => Volatile.Read(ref connectPackets);
        public int Port { get; }
        public TlsProbeBroker(X509Certificate2 certificate)
        {
            this.certificate = certificate; listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            accept = Accept();
        }
        private async Task Accept()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    Interlocked.Increment(ref connections); clients.Add(Handle(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            using (var ssl = new SslStream(client.GetStream()))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
                    var single = new byte[1];
                    await ssl.ReadExactlyAsync(single, deadline.Token);
                    Interlocked.Increment(ref applicationStreams);
                    if (single[0] != 0x10) throw new InvalidDataException("Expected MQTT CONNECT");
                    var length = 0; var multiplier = 1;
                    for (var i = 0; i < 4; i++)
                    {
                        await ssl.ReadExactlyAsync(single, deadline.Token);
                        length += (single[0] & 127) * multiplier;
                        if ((single[0] & 128) == 0) break;
                        multiplier *= 128;
                    }
                    if (length is < 10 or > 4096) throw new InvalidDataException("Invalid MQTT CONNECT length");
                    var payload = new byte[length]; await ssl.ReadExactlyAsync(payload, deadline.Token);
                    Interlocked.Increment(ref connectPackets);
                    await ssl.WriteAsync(new byte[] { 0x20, 3, 0, 0, 0 }, deadline.Token);
                    await ssl.FlushAsync(deadline.Token);
                    var buffer = new byte[256];
                    while (await ssl.ReadAsync(buffer, deadline.Token) != 0) { }
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) { }
                finally { Interlocked.Increment(ref completedConnections); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync(); listener.Stop();
            await accept; await Task.WhenAll(clients); lifetime.Dispose();
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
