using Tmds.DBus.Protocol;

namespace Shade;

// A dedicated connection owns the portal session. Dispose releases its registrations.
// No X11 key grabs: activation must cover native Wayland applications too.
internal sealed class WaylandRecovery : IDisposable
{
    private const string Service = "org.freedesktop.portal.Desktop", Path = "/org/freedesktop/portal/desktop";
    private const string Interface = "org.freedesktop.portal.GlobalShortcuts", Shortcut = "restore-all";
    private const ObserverFlags WatchFlags = ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed;
    private readonly DBusConnection bus;
    private readonly Action activated;
    private readonly Action? changed;
    private readonly List<IDisposable> watches = [];
    private readonly TaskCompletionSource lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string owner = "", session = "", trigger = "";
    private int ready, disposed;
    internal bool Ready => Volatile.Read(ref ready) != 0 && !lost.Task.IsCompleted;
    internal string TriggerDescription => Volatile.Read(ref trigger);
    internal Task Lost => lost.Task;
    private WaylandRecovery(string address, Action activated, Action? changed) { bus = new(address); this.activated = activated; this.changed = changed; }
    internal static async Task<WaylandRecovery> Create(Action activated, CancellationToken token, string? testBusAddress = null, Action? changed = null)
    {
        var recovery = new WaylandRecovery(testBusAddress ?? DBusAddress.Session ?? throw new NotSupportedException("No desktop session bus."), activated, changed);
        try { await recovery.Initialize(token); return recovery; }
        catch { recovery.Dispose(); throw; }
    }
    private async Task Initialize(CancellationToken token)
    {
        using var cancellation = token.Register(bus.Dispose);
        await bus.ConnectAsync().AsTask().WaitAsync(token);
        var version = await bus.CallMethodAsync(VersionMessage(), static (m, _) => m.GetBodyReader().ReadVariantValue().GetUInt32(), null).WaitAsync(token);
        if (version < 1) throw new NotSupportedException("Global Shortcuts portal is unavailable.");
        owner = await Owner(token);
        watches.Add(await Watch("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged",
            static (m, _) => { var r = m.GetBodyReader(); return (r.ReadString(), r.ReadString(), r.ReadString()); },
            (Exception? error, (string Name, string Old, string New) change) =>
            { if (error is not null || (change.Name == Service && change.Old == owner && change.New != owner)) Invalidate(); }));
        if (await Owner(token) != owner) throw new IOException("Shortcut portal restarted during setup.");
        _ = MonitorDisconnect();
        var sessionToken = "shade_" + Guid.NewGuid().ToString("N");
        var result = await Request("CreateSession", null, sessionToken, token);
        var expected = "/org/freedesktop/portal/desktop/session/" + SenderElement() + "/" + sessionToken;
        if (!result.TryGetValue("session_handle", out var handle) || handle.GetString() != expected)
            throw new InvalidDataException("Unexpected shortcut session handle.");
        session = expected;
        watches.Add(await Watch(owner, session, "org.freedesktop.portal.Session", "Closed",
            static (m, _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue(),
            (Exception? _, Dictionary<string, VariantValue> _) => Invalidate()));
        watches.Add(await Watch(owner, Path, Interface, "Activated",
            static (m, _) =>
            {
                var r = m.GetBodyReader(); var path = r.ReadObjectPath().ToString(); var id = r.ReadString();
                _ = r.ReadUInt64(); _ = r.ReadDictionaryOfStringToVariantValue(); return (path, id);
            },
            (Exception? error, (string Session, string Id) value) =>
            {
                if (error is not null) Invalidate();
                else if (value.Session == session && value.Id == Shortcut && Ready)
                { try { activated(); } catch { Invalidate(); } }
            }));
        watches.Add(await Watch(owner, Path, Interface, "ShortcutsChanged",
            static (m, _) =>
            {
                var r = m.GetBodyReader(); var path = r.ReadObjectPath().ToString();
                return (path, ReadShortcuts(ref r));
            },
            (Exception? error, (string Session, string Trigger) value) =>
            {
                if (error is not null) Invalidate();
                else if (value.Session == session)
                {
                    if (string.IsNullOrWhiteSpace(value.Trigger)) Invalidate();
                    else { Volatile.Write(ref trigger, value.Trigger); try { changed?.Invoke(); } catch { Invalidate(); } }
                }
            }));
        result = await Request("BindShortcuts", session, null, token);
        if (!result.TryGetValue("shortcuts", out var shortcuts)) throw new InvalidDataException("Recovery shortcut was not returned.");
        trigger = ShortcutDescription(shortcuts);
        if (string.IsNullOrWhiteSpace(trigger) || lost.Task.IsCompleted) throw new InvalidOperationException("Recovery shortcut was not granted.");
        Volatile.Write(ref ready, 1);
    }
    private async Task<Dictionary<string, VariantValue>> Request(string method, string? sessionPath, string? sessionToken, CancellationToken token)
    {
        var requestToken = "shade_" + Guid.NewGuid().ToString("N");
        var path = "/org/freedesktop/portal/desktop/request/" + SenderElement() + "/" + requestToken;
        var completion = new TaskCompletionSource<Dictionary<string, VariantValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await Watch(owner, path, "org.freedesktop.portal.Request", "Response",
            static (m, _) => { var r = m.GetBodyReader(); return (r.ReadUInt32(), r.ReadDictionaryOfStringToVariantValue()); },
            (Exception? error, (uint Code, Dictionary<string, VariantValue> Values) value) =>
            {
                if (error is not null) completion.TrySetException(error);
                else if (value.Code != 0) completion.TrySetException(new InvalidOperationException("Recovery shortcut setup was cancelled or denied."));
                else completion.TrySetResult(value.Values);
            });
        var returned = await bus.CallMethodAsync(RequestMessage(method, requestToken, sessionPath, sessionToken),
            static (m, _) => m.GetBodyReader().ReadObjectPath().ToString(), null).WaitAsync(token);
        if (returned != path) throw new InvalidDataException("Unexpected shortcut request handle.");
        return await completion.Task.WaitAsync(token);
    }
    private string SenderElement() => (bus.UniqueName ?? throw new IOException("No D-Bus connection identity.")).TrimStart(':').Replace('.', '_');
    private async Task<IDisposable> Watch<T>(string sender, string path, string iface, string signal,
        MessageValueReader<T> reader, Action<Exception?, T> handler)
        => await bus.WatchSignalAsync(sender, path, iface, signal, reader,
            (Notification<T> notification) =>
            {
                if (notification.HasValue) handler(null, notification.Value);
                else if (notification.IsCompletion) handler(notification.Exception ?? new IOException("Portal observer ended."), default!);
            }, flags: WatchFlags, emitOnCapturedContext: false);
    private Task<string> Owner(CancellationToken token)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner", "s");
        w.WriteString(Service);
        return bus.CallMethodAsync(w.CreateMessage(), static (m, _) => m.GetBodyReader().ReadString(), null).WaitAsync(token);
    }
    private MessageBuffer VersionMessage()
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(Service, Path, "org.freedesktop.DBus.Properties", "Get", "ss");
        w.WriteString(Interface); w.WriteString("version"); return w.CreateMessage();
    }
    private MessageBuffer RequestMessage(string method, string requestToken, string? sessionPath, string? sessionToken)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader(owner, Path, Interface, method, sessionPath is null ? "a{sv}" : "oa(sa{sv})sa{sv}");
        if (sessionPath is not null)
        {
            w.WriteObjectPath(sessionPath);
            var array = w.WriteArrayStart(DBusType.Struct); w.WriteStructureStart(); w.WriteString(Shortcut);
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["description"] = "Restore all Shade screens", ["preferred_trigger"] = "CTRL+ALT+SHIFT+r" });
            w.WriteArrayEnd(array); w.WriteString("");
        }
        var options = new Dictionary<string, VariantValue> { ["handle_token"] = requestToken };
        if (sessionToken is not null) options["session_handle_token"] = sessionToken;
        w.WriteDictionary(options); return w.CreateMessage();
    }
    private static string ReadShortcuts(ref Reader reader)
    {
        var result = ""; var end = reader.ReadArrayStart(DBusType.Struct); var count = 0;
        while (reader.HasNext(end))
        {
            if (++count > 1024) throw new InvalidDataException("Too many shortcut bindings.");
            reader.AlignStruct(); var id = reader.ReadString(); var properties = reader.ReadDictionaryOfStringToVariantValue();
            if (id == Shortcut && properties.TryGetValue("trigger_description", out var description)) result = description.GetString();
        }
        return result;
    }
    private static string ShortcutDescription(VariantValue shortcuts)
    {
        if (shortcuts.Type != VariantValueType.Array || shortcuts.Count > 1024) throw new InvalidDataException("Invalid shortcut bindings.");
        for (var i = 0; i < shortcuts.Count; i++)
        {
            var item = shortcuts.GetItem(i);
            if (item.GetItem(0).GetString() == Shortcut)
            {
                var values = item.GetItem(1).GetDictionary<string, VariantValue>();
                if (values.TryGetValue("trigger_description", out var description)) return description.GetString();
            }
        }
        return "";
    }
    private async Task MonitorDisconnect() { await bus.DisconnectedAsync(); if (Volatile.Read(ref disposed) == 0) Invalidate(); }
    private void Invalidate() { Volatile.Write(ref ready, 0); lost.TrySetResult(); }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Invalidate(); foreach (var watch in watches) watch.Dispose(); bus.Dispose();
    }
}
