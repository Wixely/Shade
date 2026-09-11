using System.Diagnostics;
using Shade;
using Tmds.DBus.Protocol;

internal static class WaylandRecoveryTests
{
    internal static async Task Run()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var start = new ProcessStartInfo("dbus-daemon") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--session"); start.ArgumentList.Add("--nofork"); start.ArgumentList.Add("--print-address=1");
        using var daemon = Process.Start(start) ?? throw new Exception("Isolated test bus did not start");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var token = timeout.Token;
            var address = await daemon.StandardOutput.ReadLineAsync(token) ?? throw new Exception("No test bus address");
            using var service = new DBusConnection(address);
            await service.ConnectAsync();
            await service.RequestNameAsync("org.freedesktop.portal.Desktop");
            var portal = new Portal(service); service.AddMethodHandler(portal);
            var count = 0;
            var activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var recovery = await WaylandRecovery.Create(() => { Interlocked.Increment(ref count); activation.TrySetResult(); }, token, address))
            {
                Check(recovery.Ready && recovery.TriggerDescription == "Ctrl+Alt+Shift+R", "Portal binding was not confirmed");
                using var stranger = new DBusConnection(address); await stranger.ConnectAsync();
                portal.Activate(stranger, portal.Session, "restore-all");
                portal.Activate(service, "/unrelated", "restore-all");
                portal.Activate(service, portal.Session, "unrelated");
                portal.Activate(service, portal.Session, "restore-all");
                await activation.Task.WaitAsync(token);
                Check(Volatile.Read(ref count) == 1, "Recovery accepted another sender, session or shortcut");
                portal.Changed(false);
                await recovery.Lost.WaitAsync(token);
                Check(!recovery.Ready, "Removed recovery binding remained usable");
            }
            portal.Deny = true;
            try { using var denied = await WaylandRecovery.Create(() => { }, token, address); throw new Exception("Denied recovery was accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("denied", StringComparison.Ordinal)) { }
            portal.Deny = false; portal.OmitBinding = true;
            try { using var missing = await WaylandRecovery.Create(() => { }, token, address); throw new Exception("Empty binding was accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not granted", StringComparison.Ordinal)) { }
            portal.OmitBinding = false;
            portal.WithholdResponse = true;
            using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                try { using var stalled = await WaylandRecovery.Create(() => { }, cancelled.Token, address); throw new Exception("Stalled registration completed"); }
                catch (Exception) when (cancelled.IsCancellationRequested) { }
            }
            portal.WithholdResponse = false;
            using (var closed = await WaylandRecovery.Create(() => { }, token, address))
            {
                portal.CloseSession(); await closed.Lost.WaitAsync(token); Check(!closed.Ready, "Closed session remained usable");
            }
            using (var lost = await WaylandRecovery.Create(() => { }, token, address))
            {
                service.Dispose(); await lost.Lost.WaitAsync(token); Check(!lost.Ready, "Lost portal owner remained usable");
            }
            Console.WriteLine("Isolated portal: binding, activation filtering, denied/empty binding, cancellation, shortcut removal, session close and owner loss passed.");
        }
        finally { if (!daemon.HasExited) { daemon.Kill(); await daemon.WaitForExitAsync(); } }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal sealed class Portal(DBusConnection bus) : IPathMethodHandler
    {
        private const string Interface = "org.freedesktop.portal.GlobalShortcuts";
        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        internal bool Deny, OmitBinding, WithholdResponse;
        internal int CreateCount;
        internal string Session = "";
        private string client = "";
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try { Handle(context); }
            catch { context.ReplyError("org.freedesktop.DBus.Error.Failed", "Invalid test portal request"); }
            return default;
        }
        private void Handle(MethodContext context)
        {
            var message = context.Request; var reader = message.GetBodyReader();
            if (message.MemberAsString == "Get")
            {
                Check(reader.ReadString() == Interface && reader.ReadString() == "version", "Wrong property request");
                using var reply = context.CreateReplyWriter("v"); reply.WriteVariantUInt32(1); context.Reply(reply.CreateMessage()); return;
            }
            client = message.SenderAsString ?? throw new Exception("Missing sender");
            var sender = client.TrimStart(':').Replace('.', '_');
            Dictionary<string, VariantValue> options;
            var create = message.MemberAsString == "CreateSession";
            if (create)
            {
                Interlocked.Increment(ref CreateCount);
                options = reader.ReadDictionaryOfStringToVariantValue();
                Session = Path + "/session/" + sender + "/" + options["session_handle_token"].GetString();
            }
            else
            {
                Check(message.MemberAsString == "BindShortcuts" && reader.ReadObjectPath().ToString() == Session, "Wrong binding session");
                var array = reader.ReadArrayStart(DBusType.Struct); Check(reader.HasNext(array), "No shortcut requested"); reader.AlignStruct();
                Check(reader.ReadString() == "restore-all", "Wrong action");
                var properties = reader.ReadDictionaryOfStringToVariantValue();
                Check(properties["preferred_trigger"].GetString() == "CTRL+ALT+SHIFT+r" && !reader.HasNext(array), "Wrong preferred shortcut");
                Check(reader.ReadString() == "", "Unexpected window identifier"); options = reader.ReadDictionaryOfStringToVariantValue();
            }
            var path = Path + "/request/" + sender + "/" + options["handle_token"].GetString();
            if (WithholdResponse)
            {
                using var pending = context.CreateReplyWriter("o"); pending.WriteObjectPath(path); context.Reply(pending.CreateMessage()); return;
            }
            // Deliberately signal before the method reply to verify race-free subscription.
            var signal = bus.GetMessageWriter();
            try
            {
                signal.WriteSignalHeader(client, path, "org.freedesktop.portal.Request", "Response", "ua{sv}");
                signal.WriteUInt32(Deny && !create ? 1u : 0u);
                var dict = signal.WriteDictionaryStart(); signal.WriteDictionaryEntryStart();
                if (create) { signal.WriteString("session_handle"); signal.WriteVariantString(Session); }
                else { signal.WriteString("shortcuts"); signal.WriteSignature("a(sa{sv})"); WriteShortcuts(ref signal, !OmitBinding); }
                signal.WriteDictionaryEnd(dict); Check(bus.TrySendMessage(signal.CreateMessage()), "Response send failed");
            }
            finally { signal.Dispose(); }
            using var response = context.CreateReplyWriter("o"); response.WriteObjectPath(path); context.Reply(response.CreateMessage());
        }
        internal void Activate(DBusConnection sender, string session, string shortcut)
        {
            using var w = sender.GetMessageWriter(); w.WriteSignalHeader(client, Path, Interface, "Activated", "osta{sv}");
            w.WriteObjectPath(session); w.WriteString(shortcut); w.WriteUInt64(1); w.WriteDictionary(new Dictionary<string, VariantValue>());
            Check(sender.TrySendMessage(w.CreateMessage()), "Activation send failed");
        }
        internal void Changed(bool bound)
        {
            var w = bus.GetMessageWriter();
            try
            {
                w.WriteSignalHeader(client, Path, Interface, "ShortcutsChanged", "oa(sa{sv})");
                w.WriteObjectPath(Session); WriteShortcuts(ref w, bound); Check(bus.TrySendMessage(w.CreateMessage()), "Changed send failed");
            }
            finally { w.Dispose(); }
        }
        internal void CloseSession()
        {
            using var w = bus.GetMessageWriter(); w.WriteSignalHeader(client, Session, "org.freedesktop.portal.Session", "Closed", "a{sv}");
            w.WriteDictionary(new Dictionary<string, VariantValue>()); Check(bus.TrySendMessage(w.CreateMessage()), "Close send failed");
        }
        private static void WriteShortcuts(ref MessageWriter w, bool bound)
        {
            var array = w.WriteArrayStart(DBusType.Struct);
            if (bound)
            {
                w.WriteStructureStart(); w.WriteString("restore-all");
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["trigger_description"] = "Ctrl+Alt+Shift+R" });
            }
            w.WriteArrayEnd(array);
        }
    }
}
