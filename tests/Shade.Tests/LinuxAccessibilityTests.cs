using Tmds.DBus.Protocol;
using System.Net;
using System.Net.Sockets;
using MQTTnet.Protocol;
using MQTTnet.Server;

internal static class LinuxAccessibilityTests
{
    private const string Accessible = "org.a11y.atspi.Accessible";
    private const string Value = "org.a11y.atspi.Value";
    private const string Action = "org.a11y.atspi.Action";
    private const string Properties = "org.freedesktop.DBus.Properties";
    private sealed record Node(string Path, string Name, string[] Interfaces);

    internal static async Task Run(int process, CancellationToken token, Func<string, Task>? capture = null, Func<string, Task>? type = null, Func<bool, Task>? tab = null, bool keyringUnavailable = false)
    {
        using var session = new DBusConnection(DBusAddress.Session ?? throw new Exception("No accessibility session bus"));
        await session.ConnectAsync().AsTask().WaitAsync(token);
        var address = await Call(session, "org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus", "GetAddress", null,
            static (m, _) => m.GetBodyReader().ReadString(), token);
        using var bus = new DBusConnection(address);
        await bus.ConnectAsync().AsTask().WaitAsync(token);
        string? owner = null;
        while (owner is null)
        {
            var names = await Call(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "ListNames", null, Strings, token);
            foreach (var name in names.Where(n => n.StartsWith(':')))
            {
                var pid = await Call(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetConnectionUnixProcessID", "s",
                    static (m, _) => m.GetBodyReader().ReadUInt32(), token, [name]);
                if (pid == process) { owner = name; break; }
            }
            if (owner is null) await Task.Delay(100, token);
        }

        async Task<List<Node>> ReadTree()
        {
            var nodes = new List<Node>();
            var pending = new Queue<string>(); pending.Enqueue("/org/a11y/atspi/accessible/root");
            var seen = new HashSet<string>();
            while (pending.TryDequeue(out var path))
            {
                if (!seen.Add(path)) continue;
                Check(seen.Count <= 2048, "Unexpected accessibility tree size");
                var name = await Call(bus, owner, path, Properties, "Get", "ss",
                    static (m, _) => m.GetBodyReader().ReadVariantValue().GetString(), token, [Accessible, "Name"]);
                var interfaces = await Call(bus, owner, path, Accessible, "GetInterfaces", null, Strings, token);
                nodes.Add(new(path, name, interfaces));
                var children = await Call(bus, owner, path, Accessible, "GetChildren", null, References, token);
                foreach (var child in children)
                    if (child.Owner == owner && child.Path != "/org/a11y/atspi/null") pending.Enqueue(child.Path);
            }
            return nodes;
        }
        async Task<List<Node>> Tree()
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await ReadTree(); }
                catch (DBusErrorReplyException ex) when (attempt < 3 && ex.ErrorName is
                    "org.freedesktop.DBus.Error.UnknownObject" or "org.freedesktop.DBus.Error.UnknownMethod")
                { await Task.Delay(50, token); }
            }
        }
        async Task<List<Node>> Until(Func<List<Node>, bool> condition)
        {
            while (true)
            {
                var nodes = await Tree();
                if (condition(nodes)) return nodes;
                await Task.Delay(100, token);
            }
        }
        Task<double> Read(Node node) => Call(bus, owner, node.Path, Properties, "Get", "ss",
            static (m, _) => m.GetBodyReader().ReadVariantValue().GetDouble(), token, [Value, "CurrentValue"]);
        Task<bool> Checked(Node node) => Call(bus, owner, node.Path, Accessible, "GetState", null,
            static (m, _) => { var r = m.GetBodyReader(); var a = r.ReadArrayStart(DBusType.UInt32); return r.HasNext(a) && (r.ReadUInt32() & (1u << 4)) != 0; }, token);
        Task<bool> Set(Node node, double value) => Call(bus, owner, node.Path, Properties, "Set", "ssv",
            static (_, _) => true, token, [Value, "CurrentValue"], number: value);
        async Task Activate(Node node) => Check(await Call(bus, owner, node.Path, "org.a11y.atspi.Action", "DoAction", "i",
            static (m, _) => m.GetBodyReader().ReadBool(), token, action: 0), "Accessibility action was rejected");
        async Task Focus(Node node)
        {
            Check(await Call(bus, owner, node.Path, "org.a11y.atspi.Component", "GrabFocus", null,
                static (m, _) => m.GetBodyReader().ReadBool(), token), "Broker focus rejected");
            while (!await Call(bus, owner, node.Path, Accessible, "GetState", null,
                static (m, _) => { var r = m.GetBodyReader(); var a = r.ReadArrayStart(DBusType.UInt32); return r.HasNext(a) && (r.ReadUInt32() & (1u << 12)) != 0; }, token))
                await Task.Delay(50, token);
        }
        static Node Named(List<Node> nodes, string name)
        {
            var matches = nodes.Where(n => n.Name == name && (n.Interfaces.Contains(Action) || n.Interfaces.Contains(Value))).ToArray();
            Check(matches.Length == 1, "Expected one accessible control: " + name + "; found " + matches.Length);
            return matches[0];
        }
        static Node[] Screens(List<Node> nodes) => nodes.Where(n => n.Interfaces.Contains(Action) && n.Name.StartsWith("Screen ", StringComparison.Ordinal) && n.Name.Contains(", dimming ")).ToArray();
        static void Disabled(List<Node> nodes)
        {
            var screens = Screens(nodes);
            Check(screens.Length > 0 && screens.All(n => n.Name.EndsWith(", dimming off", StringComparison.Ordinal)), "Disabled screen controls are missing or a global change enabled a screen");
        }

        var tree = await Until(n => n.Any(x => x.Name == "Global dimming") && Screens(n).Length > 0);
        var count = Screens(tree).Length; Disabled(tree);
        Console.WriteLine("AT-SPI: located owned application and disabled screen controls");
        var global = Named(tree, "Global dimming");
        await Set(global, 41);
        while (await Read(global) != 41) await Task.Delay(50, token);
        tree = await Tree(); Disabled(tree);
        Console.WriteLine("AT-SPI: global value updated without enabling screens");
        await Activate(tree.Single(n => n.Interfaces.Contains(Action) && n.Name.StartsWith("Advanced", StringComparison.Ordinal)));
        tree = await Until(n => n.Count(x => x.Interfaces.Contains(Value) && x.Name.StartsWith("Dimming for ", StringComparison.Ordinal)) == count);
        Console.WriteLine("AT-SPI: Advanced exposes every individual slider");
        foreach (var slider in tree.Where(n => n.Interfaces.Contains(Value) && n.Name.StartsWith("Dimming for ", StringComparison.Ordinal)))
            Check(await Read(slider) == 0, "Disabled screen acquired nonzero dimming");
        var membership = Named(tree, "Use global for screen 1");
        Check(await Checked(membership), "Initial global membership is not checked");
        await Activate(membership);
        while (await Checked(membership)) await Task.Delay(50, token);
        tree = await Tree();
        Console.WriteLine("AT-SPI: per-screen global membership changed");
        await Set(Named(tree, "Global dimming"), 42);
        while (await Read(global) != 42) await Task.Delay(50, token);
        tree = await Tree(); Disabled(tree);
        Check(!await Checked(membership), "Global slider reset independent membership");
        Console.WriteLine("AT-SPI: independent membership survived another global change");
        if (capture is not null) await capture("controls-global-opt-out.png");
        await Activate(Named(tree, "Home Assistant"));
        tree = await Until(n => n.Any(x => x.Name == "Test connection") && n.Any(x => x.Name == "Back to screens"));
        foreach (var name in new[] { "Broker hostname", "Broker port", "Broker username", "Broker password" })
        {
            var fields = tree.Where(n => n.Name == name).ToArray();
            var roles = new List<uint>();
            foreach (var field in fields)
            {
                var role = await Call(bus, owner, field.Path, Accessible, "GetRole", null,
                    static (m, _) => m.GetBodyReader().ReadUInt32(), token);
                roles.Add(role);
                if (role == 40u)
                {
                    var roleName = await Call(bus, owner, field.Path, Accessible, "GetRoleName", null,
                        static (m, _) => m.GetBodyReader().ReadString(), token);
                    Check(roleName == "password text", "Password role name is missing");
                    var editable = await Call(bus, owner, field.Path, Accessible, "GetState", null,
                        static (m, _) => { var r = m.GetBodyReader(); var a = r.ReadArrayStart(DBusType.UInt32); return r.HasNext(a) && (r.ReadUInt32() & (1u << 7)) != 0; }, token);
                    Check(editable, "Password field lost editable state");
                }
            }
            var expected = name == "Broker password" ? 40u : 79u;
            Check(roles.Count(r => r == expected) == 1, "Expected one correctly typed broker input: " + name);
            if (name == "Broker password") Check(!roles.Contains(79u), "Password exposed as ordinary entry");
        }
        Console.WriteLine("AT-SPI: broker inputs and protected password role verified");
        if (type is not null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var broker = new MqttServerFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
                .WithDefaultEndpointPort(port).WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointBoundIPV6Address(IPAddress.IPv6Loopback).Build());
            var authenticated = 0;
            var rejected = 0;
            broker.ValidatingConnectionAsync += e =>
            {
                if (e.UserName == "synthetic-user" && e.Password == "synthetic-password" && e.ClientId.EndsWith("_test", StringComparison.Ordinal))
                    Interlocked.Increment(ref authenticated);
                else { e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword; Interlocked.Increment(ref rejected); }
                return Task.CompletedTask;
            };
            await broker.StartAsync();
            try
            {
                var tls = Named(tree, "Use TLS");
                if (await Checked(tls)) await Activate(tls);
                while (await Checked(tls)) await Task.Delay(50, token);
                // Start with a clipped field, then return to the first field. Check both
                // directions of focus-driven scrolling in the fixed 720x740 test window.
                await Focus(tree.Single(n => n.Name == "Broker password"));
                foreach (var (name, contents) in new[] { ("Broker hostname", "127.0.0.1"),
                    ("Broker port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("Broker username", "synthetic-user"), ("Broker password", "synthetic-password") })
                {
                    tree = await Tree();
                    var field = tree.Single(n => n.Name == name);
                    await Focus(field);
                    var bounds = await Call(bus, owner, field.Path, "org.a11y.atspi.Component", "GetExtents", "u",
                        static (m, _) => { var r = m.GetBodyReader(); r.AlignStruct(); return (X: r.ReadInt32(), Y: r.ReadInt32(), W: r.ReadInt32(), H: r.ReadInt32()); }, token, coordinateType: 1);
                    Check(bounds.Y >= 308 && bounds.Y + bounds.H <= 700, "Focused broker input lacks room inside the content viewport: " + name);
                    await type(contents);
                    Console.WriteLine("Native typing: entered " + name);
                }
                tree = await Tree();
                Check(tree.All(n => !n.Name.Contains("synthetic-password", StringComparison.Ordinal)), "Password leaked into accessibility names");
                if (capture is not null) await capture("broker-form.png");
                await Activate(Named(tree, "Test connection"));
                tree = await Until(n => n.Any(x => x.Name.StartsWith("Broker connection succeeded", StringComparison.Ordinal)) ||
                    n.Any(x => x.Name.StartsWith("Connection test failed", StringComparison.Ordinal)));
                Check(Volatile.Read(ref authenticated) == 1, "Broker did not receive the typed credentials");
                Check(tree.Any(n => n.Name.StartsWith("Broker connection succeeded", StringComparison.Ordinal)), "UI did not confirm broker connection");
                foreach (var valid in new[] { false, true })
                {
                    var password = tree.Single(n => n.Name == "Broker password");
                    await Focus(password);
                    await type(valid ? "synthetic-password" : "wrong-password");
                    await Activate(Named(await Tree(), "Test connection"));
                    // Each result must change from the preceding result; stale success cannot pass.
                    var resultPrefix = valid ? "Broker connection succeeded" : "Connection test failed";
                    tree = await Until(n => n.Any(x => x.Name.StartsWith(resultPrefix, StringComparison.Ordinal)));
                    Check(Volatile.Read(ref authenticated) == (valid ? 2 : 1), "Unexpected authentication after password correction");
                    Check(Volatile.Read(ref rejected) == 1, "Broker did not reject the incorrect password exactly once");
                }
                Console.WriteLine("PASS Linux broker form: native typing, masked accessibility names, authentication, rejection and password correction");
                if (keyringUnavailable)
                {
                    await Activate(Named(tree, "Save and enable"));
                    tree = await Until(n => n.Any(x => x.Name.Contains("requires libsecret", StringComparison.Ordinal)));
                    Check(tree.Any(n => n.Name.Contains("No password was saved", StringComparison.Ordinal)), "Missing keyring error did not explain unsaved password");
                    Check(Volatile.Read(ref authenticated) == 2 && Volatile.Read(ref rejected) == 1,
                        "Failed keyring save attempted an integration connection");
                    Console.WriteLine("PASS Linux missing-keyring UI reports failure without connecting");
                }
            }
            finally { await broker.StopAsync(); }
        }
        if (tab is not null)
        {
            tree = await Tree();
            await Focus(tree.Single(n => n.Name == "Broker hostname"));
            var order = new[] { "Broker hostname", "Broker port", "Use TLS", "Broker username", "Broker password", "Forget saved password", "Test connection", "Save and enable" };
            foreach (var reverse in new[] { false, true })
            {
                foreach (var name in reverse ? order.Reverse().Skip(1) : order.Skip(1))
                {
                    await tab(reverse);
                    tree = await Tree();
                    var node = tree.Single(n => n.Name == name);
                    while (!await Call(bus, owner, node.Path, Accessible, "GetState", null,
                        static (m, _) => { var r = m.GetBodyReader(); var a = r.ReadArrayStart(DBusType.UInt32); return r.HasNext(a) && (r.ReadUInt32() & (1u << 12)) != 0; }, token))
                        await Task.Delay(50, token);
                    var box = await Call(bus, owner, node.Path, "org.a11y.atspi.Component", "GetExtents", "u",
                        static (m, _) => { var r = m.GetBodyReader(); r.AlignStruct(); return (X: r.ReadInt32(), Y: r.ReadInt32(), W: r.ReadInt32(), H: r.ReadInt32()); }, token, coordinateType: 1);
                    Check(box.Y >= 296 && box.Y + box.H <= 712, "Native Tab left its target clipped: " + name);
                }
                if (!reverse && capture is not null) await capture("keyboard-save-focus.png");
            }
            Console.WriteLine("PASS Linux native Tab and Shift+Tab reveal broker inputs and actions");
        }
        await Activate(Named(tree, "Back to screens"));
        tree = await Until(n => Screens(n).Length == count); Disabled(tree);
        Check(await Read(Named(tree, "Global dimming")) == 42, "Navigation lost global value");
        Console.WriteLine("PASS Linux AT-SPI: global values, disabled-screen exclusion, Advanced sliders, global opt-out and integration navigation");
    }

    private static Task<T> Call<T>(DBusConnection bus, string owner, string path, string iface, string member, string? signature,
        MessageValueReader<T> reader, CancellationToken token, string[]? strings = null, int? action = null, double? number = null, uint? coordinateType = null)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(owner, path, iface, member, signature);
        if (strings is not null) foreach (var value in strings) writer.WriteString(value);
        if (action is { } index) writer.WriteInt32(index);
        if (number is { } amount) writer.WriteVariantDouble(amount);
        if (coordinateType is { } coordinate) writer.WriteUInt32(coordinate);
        return bus.CallMethodAsync(writer.CreateMessage(), reader, null).WaitAsync(token);
    }
    private static string[] Strings(Message message, object? state)
    {
        var reader = message.GetBodyReader(); var array = reader.ReadArrayStart(DBusType.String);
        var values = new List<string>(); while (reader.HasNext(array)) values.Add(reader.ReadString()); return values.ToArray();
    }
    private static (string Owner, string Path)[] References(Message message, object? state)
    {
        var reader = message.GetBodyReader(); var array = reader.ReadArrayStart(DBusType.Struct);
        var values = new List<(string, string)>();
        while (reader.HasNext(array)) { reader.AlignStruct(); values.Add((reader.ReadString(), reader.ReadObjectPath().ToString())); }
        return values.ToArray();
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
