using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Shade;

internal static class TlsTrustTests
{
    // Child-scoped OpenSSL trust paths exercise Shade's default validation without custom callbacks.
    internal static async Task Run()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated OpenSSL trust checks require Linux.");
        var directory = Path.Combine(Path.GetTempPath(), "Shade.TlsTrust-" + Guid.NewGuid().ToString("N"));
        var empty = Path.Combine(directory, "empty"); Directory.CreateDirectory(empty);
        var rootFile = Path.Combine(directory, "root.pem");
        using var key = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Shade test root " + Guid.NewGuid().ToString("N"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-30), now.AddDays(30));
        try
        {
            await File.WriteAllTextAsync(rootFile, root.ExportCertificatePem());
            foreach (var scenario in new[] { "valid", "wrong-host", "expired", "not-yet-valid", "untrusted", "revoked" })
            {
                await using var revocation = new CrlServer();
                using var leafKey = RSA.Create(2048);
                var request = new CertificateRequest("CN=Shade test broker", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                var names = new SubjectAlternativeNameBuilder();
                if (scenario == "wrong-host") names.AddDnsName("unrelated.invalid"); else names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([revocation.Uri]));
                var before = scenario == "not-yet-valid" ? now.AddDays(1) : now.AddDays(-2);
                var after = scenario == "expired" ? now.AddDays(-1) : now.AddDays(2);
                using var issued = request.Create(root, before, after, RandomNumberGenerator.GetBytes(16));
                using var certificate = issued.CopyWithPrivateKey(leafKey);
                var crl = new CertificateRevocationListBuilder();
                if (scenario == "revoked") crl.AddEntry(certificate, now.AddHours(-1));
                revocation.Content = crl.Build(root, 1, now.AddDays(1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1, now.AddHours(-2));
                await using var broker = new TlsTests.TlsProbeBroker(certificate);
                var executable = Environment.ProcessPath ?? throw new Exception("No test executable");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(executable) == "dotnet") start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
                start.ArgumentList.Add("--tls-trust-child"); start.ArgumentList.Add(broker.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(scenario == "valid" ? "success" : "failure");
                start.Environment["SSL_CERT_FILE"] = scenario == "untrusted" ? Path.Combine(empty, "missing.pem") : rootFile;
                start.Environment["SSL_CERT_DIR"] = empty;
                // .NET may cache public CRLs in its normal user cache. Unique issuers and URLs
                // prevent an earlier run's cache from affecting this test; no trust store is edited.
                using var child = Process.Start(start) ?? throw new Exception("Trust-test child did not start");
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var output = child.StandardOutput.ReadToEndAsync(deadline.Token);
                    var errors = child.StandardError.ReadToEndAsync(deadline.Token);
                    await child.WaitForExitAsync(deadline.Token);
                    Check(child.ExitCode == 0, scenario + ": " + await output + await errors);
                    Check((await errors).Length == 0, "Unexpected trust-test stderr");
                    var until = Stopwatch.StartNew();
                    while (broker.CompletedConnections < 1 && until.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10, deadline.Token);
                    var expected = scenario == "valid" ? 1 : 0;
                    Check(broker.CompletedConnections == 1 && broker.ApplicationStreams == expected && broker.ConnectPackets == expected,
                        "Unexpected TLS application-data transmission for " + scenario);
                    if (scenario is "valid" or "revoked")
                        Check(revocation.Requests > 0, "Certificate revocation endpoint was not exercised for " + scenario);
                    Console.WriteLine("PASS default TLS validation: " + scenario + "; MQTT CONNECT packets=" + broker.ConnectPackets);
                }
                finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            }
        }
        finally
        {
            // All paths are beneath this test's newly created, fixed absolute temporary directory.
            Directory.Delete(directory, recursive: true);
        }
    }
    internal static async Task<int> Child(int port, bool success)
    {
        using var integration = new HomeAssistantIntegration();
        await integration.TestAsync(new("127.0.0.1", port, true, "synthetic-trust-user", "synthetic-trust-password"), false);
        var accepted = integration.Status.Contains("connection succeeded", StringComparison.OrdinalIgnoreCase);
        if (accepted != success || integration.Enabled)
        {
            Console.Error.WriteLine("Unexpected default TLS validation outcome: " + integration.Status);
            return 1;
        }
        return 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class CrlServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task serving;
        private int requests;
        internal int Requests => Volatile.Read(ref requests);
        internal byte[] Content = [];
        internal string Uri { get; }
        internal CrlServer()
        {
            listener.Start(); Uri = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/" + Guid.NewGuid().ToString("N") + ".crl";
            serving = Serve();
        }
        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true);
                    for (var line = 0; line < 32; line++)
                    {
                        var header = await reader.ReadLineAsync(deadline.Token);
                        if (string.IsNullOrEmpty(header)) break;
                        Check(header.Length < 8192, "Oversized CRL request header");
                    }
                    var body = Content;
                    var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/pkix-crl\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, deadline.Token); await stream.WriteAsync(body, deadline.Token);
                    Interlocked.Increment(ref requests);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { lifetime.Cancel(); listener.Stop(); await serving; lifetime.Dispose(); }
    }
}
