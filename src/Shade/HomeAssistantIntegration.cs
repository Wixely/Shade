using System.Buffers;
using System.Text;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace Shade;

public sealed class HomeAssistantIntegration : IDisposable
{
    private readonly AutomationSettingsStore store;
    private readonly AutomationSettings settings;
    private readonly HomeAssistantProtocol protocol;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly Channel<bool> updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<AutomationCommand> commands = Channel.CreateBounded<AutomationCommand>(128);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private volatile AutomationState state = new(30, Array.Empty<AutomationScreen>());
    private volatile string status = "Integration disabled.";
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    public string Status => store.Error is { } error ? status + " " + error : status;
    public bool SettingsNeedRecovery => store.Error is not null;
    public bool SettingsNeedBackup => store.PreservingUnreadableFile;
    public string Host => settings.Host;
    public int Port => settings.Port;
    public bool Tls => settings.Tls;
    public string Username => settings.Username;
    public bool HasPassword => settings.ProtectedPassword.Length > 0;
    public bool Enabled => settings.Enabled;

    public HomeAssistantIntegration(string? settingsPath = null)
    {
        store = new(settingsPath); settings = store.Load(); protocol = new(settings.InstallationId);
    }
    public void Update(AutomationState value) { state = value; updates.Writer.TryWrite(true); }
    public bool TryRead(out AutomationCommand? command) => commands.Reader.TryRead(out command);
    private void Report(string value) { status = value; Interlocked.Increment(ref revision); }
    public Task ResumeAsync() => settings.Enabled ? ConfigureAsync(new(settings.Host, settings.Port, settings.Tls, settings.Username, ""), true, true) : Task.CompletedTask;

    public async Task ConfigureAsync(BrokerConnection connection, bool enable, bool keepPassword)
    {
        await operations.WaitAsync();
        try
        {
            connection.Validate();
            var password = keepPassword && connection.Password.Length == 0 ? await AutomationSettingsStore.UnprotectAsync(settings.ProtectedPassword) : connection.Password;
            var protectedPassword = await AutomationSettingsStore.ProtectAsync(password);
            await StopWorkerAsync();
            var candidate = new AutomationSettings
            {
                InstallationId = settings.InstallationId, Host = connection.Host, Port = connection.Port, Tls = connection.Tls,
                Username = connection.Username, ProtectedPassword = protectedPassword, Enabled = enable,
                PublishedScreens = new HashSet<string>(settings.PublishedScreens)
            };
            if (!store.Save(candidate)) { settings.Enabled = false; Report("Integration stopped because its settings could not be saved. Previous saved settings were kept."); return; }
            settings.Host = connection.Host; settings.Port = connection.Port; settings.Tls = connection.Tls;
            settings.Username = connection.Username; settings.ProtectedPassword = protectedPassword; settings.Enabled = enable;
            if (enable)
            {
                cancellation = new();
                worker = RunAsync(connection with { Password = password }, cancellation.Token);
            }
            else Report("Integration disabled.");
        }
        catch (CredentialStoreException ex) { Report(ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException or FormatException or PlatformNotSupportedException)
        { Report("Check the broker settings and re-enter the password. Saved credentials may belong to another account."); }
        finally { operations.Release(); }
    }

    public async Task TestAsync(BrokerConnection connection, bool keepPassword)
    {
        await operations.WaitAsync();
        try
        {
            connection.Validate();
            if (keepPassword && connection.Password.Length == 0) connection = connection with { Password = await AutomationSettingsStore.UnprotectAsync(settings.ProtectedPassword) };
            using var client = new MqttClientFactory().CreateMqttClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            Report("Testing broker connection...");
            await ConnectAsync(client, Options(connection, true), timeout.Token);
            await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token);
            Report("Broker connection succeeded. Enable integration to publish discovery.");
        }
        catch (CredentialStoreException ex) { Report(ex.Message); }
        catch (Exception) { Report("Connection test failed. Check host, port, credentials, certificate trust and broker permissions."); }
        finally { operations.Release(); }
    }

    private MqttClientOptions Options(BrokerConnection connection, bool test = false)
    {
        var builder = new MqttClientOptionsBuilder().WithClientId("shade_" + settings.InstallationId + (test ? "_test" : ""))
            .WithTcpServer(connection.Host, connection.Port).WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanSession().WithKeepAlivePeriod(TimeSpan.FromSeconds(30)).WithTimeout(TimeSpan.FromSeconds(8));
        if (connection.Username.Length > 0 || connection.Password.Length > 0) builder.WithCredentials(connection.Username, connection.Password);
        if (connection.Tls) builder.WithTlsOptions(t => t.UseTls()); // System trust and hostname verification, no bypass.
        if (!test) builder.WithWillTopic(protocol.Availability).WithWillPayload("offline").WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        return builder.Build();
    }

    private async Task RunAsync(BrokerConnection connection, CancellationToken token)
    {
        var delay = 1;
        while (!token.IsCancellationRequested)
        {
            using var client = new MqttClientFactory().CreateMqttClient();
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new Dictionary<string, string>();
            var rediscover = 0;
            client.DisconnectedAsync += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
            client.ApplicationMessageReceivedAsync += e =>
            {
                if (e.ApplicationMessage.Payload.Length > 1024) return Task.CompletedTask;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                if (e.ApplicationMessage.Topic == "homeassistant/status" && payload == "online")
                { Interlocked.Exchange(ref rediscover, 1); updates.Writer.TryWrite(true); }
                else if (protocol.Parse(e.ApplicationMessage.Topic, payload, e.ApplicationMessage.Retain, state) is { } command)
                {
                    if (!commands.Writer.TryWrite(command)) Report("Command queue full; excessive remote updates ignored.");
                    Interlocked.Increment(ref revision);
                }
                return Task.CompletedTask;
            };
            try
            {
                Report("Connecting to broker...");
                await ConnectAsync(client, Options(connection), token);
                // MQTT 5 preserves the sender's retain flag, allowing all retained commands to be rejected.
                var subscription = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(protocol.Root + "/#").WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainAsPublished(true))
                    .WithTopicFilter(f => f.WithTopic("homeassistant/status")).Build();
                var result = await client.SubscribeAsync(subscription, token);
                if (result.Items.Any(i => (int)i.ResultCode >= 128)) throw new InvalidOperationException("Subscription rejected.");
                while (client.IsConnected && !token.IsCancellationRequested)
                {
                    if (Interlocked.Exchange(ref rediscover, 0) != 0) sent.Clear();
                    var count = settings.PublishedScreens.Count;
                    foreach (var publication in protocol.Build(state, settings.PublishedScreens))
                        if (!sent.TryGetValue(publication.Topic, out var old) || old != publication.Payload)
                        { await PublishAsync(client, publication, token); sent[publication.Topic] = publication.Payload; }
                    if (settings.PublishedScreens.Count != count) store.Save(settings);
                    if (!sent.ContainsKey(protocol.Availability))
                    { await PublishAsync(client, new(protocol.Availability, "online"), token); sent[protocol.Availability] = "online"; Report("Connected. Home Assistant discovery is active."); }
                    // A successful subscription alone does not make a usable connection.
                    // Preserve exponential backoff while discovery/state publications are rejected.
                    delay = 1;
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var change = updates.Reader.WaitToReadAsync(wait.Token).AsTask();
                    await Task.WhenAny(change, disconnected.Task);
                    wait.Cancel();
                    while (updates.Reader.TryRead(out _)) { }
                    if (disconnected.Task.IsCompleted) break;
                }
                if (!token.IsCancellationRequested) Report("Broker disconnected. Retrying automatically; local controls still work.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { Report("Broker unavailable or access denied. Retrying automatically; local controls still work."); }
            finally
            {
                if (client.IsConnected)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await PublishAsync(client, new(protocol.Availability, "offline"), timeout.Token); await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token); }
                    catch (Exception) { /* Closing the transport leaves broker last-will recovery active. */ }
                }
            }
            if (token.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); } catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 30);
        }
    }
    private static async Task PublishAsync(IMqttClient client, AutomationPublication publication, CancellationToken token)
    {
        var result = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(publication.Topic)
            .WithPayload(publication.Payload).WithRetainFlag().WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), token);
        if ((int)result.ReasonCode >= 128) throw new InvalidOperationException("Publish rejected.");
    }
    private static async Task ConnectAsync(IMqttClient client, MqttClientOptions options, CancellationToken token)
    {
        var result = await client.ConnectAsync(options, token);
        if (result.ResultCode != MqttClientConnectResultCode.Success) throw new InvalidOperationException("Broker rejected connection.");
    }
    public async Task DisableAsync(bool removeDiscovery)
    {
        await operations.WaitAsync();
        try
        {
            await StopWorkerAsync(); settings.Enabled = false; store.Save(settings);
            if (removeDiscovery)
            {
                using var client = new MqttClientFactory().CreateMqttClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var connection = new BrokerConnection(settings.Host, settings.Port, settings.Tls, settings.Username, await AutomationSettingsStore.UnprotectAsync(settings.ProtectedPassword));
                await ConnectAsync(client, Options(connection, true), timeout.Token);
                foreach (var publication in protocol.Remove(settings.PublishedScreens)) await PublishAsync(client, publication, timeout.Token);
                await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token);
                settings.PublishedScreens.Clear(); store.Save(settings);
            }
            Report(removeDiscovery ? "Integration disabled and discovery removed." : "Integration disabled. Entities remain unavailable.");
        }
        catch (Exception) { Report("Integration disabled. Discovery removal failed; retry when the broker is reachable."); }
        finally { operations.Release(); }
    }
    private async Task StopWorkerAsync()
    {
        if (cancellation is not null) await cancellation.CancelAsync();
        if (worker is not null) await worker;
        cancellation?.Dispose(); cancellation = null; worker = null;
        while (commands.Reader.TryRead(out _)) { }
    }
    public async Task ForgetPasswordAsync()
    {
        await operations.WaitAsync();
        try
        {
            await StopWorkerAsync();
            var previousPassword = settings.ProtectedPassword;
            settings.ProtectedPassword = ""; settings.Enabled = false;
            if (!store.Save(settings))
            {
                settings.ProtectedPassword = previousPassword;
                Report("Integration stopped, but the saved password remains. Restore folder access and retry Forget saved password.");
                return;
            }
            Report("Saved password forgotten. Integration disabled; test your settings and enable it again.");
        }
        finally { operations.Release(); }
    }
    public async Task RecoverSettingsAsync()
    {
        await operations.WaitAsync();
        try
        {
            await StopWorkerAsync(); settings.Enabled = false;
            var backup = store.PreservingUnreadableFile;
            if (!store.Recover(settings)) { Report("Integration remains off. Settings recovery failed; retry after restoring folder access."); return; }
            Report(backup ? "Original settings backed up. Integration reset and disabled. Review connection settings; previous Home Assistant entities may need removal."
                : "Integration settings saved. Review them and enable the integration when ready.");
        }
        finally { operations.Release(); }
    }
    public void Dispose() { operations.Wait(); try { StopWorkerAsync().GetAwaiter().GetResult(); } finally { operations.Release(); } }
}
