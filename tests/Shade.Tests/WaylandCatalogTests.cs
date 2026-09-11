using System.Net;
using System.Net.Sockets;
using System.Text;
using Shade;

// An owned loopback peer drives real protocol frames without changing the user's desktop.
internal static class WaylandCatalogTests
{
    internal static async Task Run()
    {
        foreach (var version in new uint[] { 2, 3 })
        {
            using var peer = await Peer.Create(version);
            await peer.Initialize();
            var catalog = peer.Catalog;
            var first = catalog.Displays.Single();
            var snapshot = catalog.Displays;
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            for (var read = 0; read < 1000; read++)
                Check(ReferenceEquals(snapshot, catalog.Displays), "Unchanged catalog rebuilt its snapshot");
            Check(GC.GetAllocatedBytesForCurrentThread() == allocationStart, "Idle catalog reads allocated memory");
            Check(first.Width == 1280 && first.Height == 720 && !first.CanRemember, "Initial logical bounds or identity trust incorrect");
            var revision = catalog.Revision;
            await peer.Event(peer.Logical, 0, Words(-720, 40));
            await peer.Event(peer.Logical, 1, Words(720, 1280));
            Check(catalog.Displays.Single() == first && catalog.Revision == revision, "Partial rotation escaped the pending batch");
            Check(ReferenceEquals(snapshot, catalog.Displays), "Incomplete batch invalidated the published snapshot");
            await peer.Done();
            var rotated = catalog.Displays.Single();
            Check(rotated.Id == first.Id && rotated.X == -720 && rotated.Y == 40 && rotated.Width == 720 && rotated.Height == 1280,
                "Rotation changed identity or lost logical geometry");
            revision = catalog.Revision;
            await peer.Done();
            Check(catalog.Revision == revision, "Unchanged done event published a change");

            await peer.Event(peer.Registry, 0, Global(30, "wl_output", 3));
            var second = await peer.ReadOutput(30);
            Check(catalog.Displays.Count == 1, "New output published before its geometry arrived");
            await peer.Event(second.Logical, 3, Text("DP-2"));
            await peer.Event(second.Logical, 0, Words(0, 0));
            await peer.Event(second.Logical, 1, Words(2560, 1440));
            await peer.Event(version == 3 ? second.Core : second.Logical, 2, []);
            Check(catalog.Displays.Count == 2 && catalog.Displays.Select(d => d.Id).Distinct().Count() == 2, "Hotplug failed");

            await peer.Event(peer.Registry, 1, Words(20));
            Check(catalog.Displays.Count == 1 && catalog.Displays.Single().Width == 2560, "Unplug retained the removed output");
            await peer.Expect(peer.Logical, 0);
            await peer.Expect(peer.Core, 0);
            revision = catalog.Revision;
            await peer.Event(peer.Logical, 1, Words(800, 600));
            Check(catalog.Revision == revision, "Queued event resurrected a removed output");

            // The connection's name survives a new registry number and different geometry.
            await peer.Event(peer.Registry, 0, Global(40, "wl_output", 3));
            var returned = await peer.ReadOutput(40);
            await peer.Event(returned.Logical, 3, Text("DP-1"));
            await peer.Event(returned.Logical, 0, Words(2560, 0));
            await peer.Event(returned.Logical, 1, Words(1920, 1080));
            await peer.Event(version == 3 ? returned.Core : returned.Logical, 2, []);
            Check(catalog.Displays.Single(d => d.X == 2560).Id == first.Id, "Replug used registry number as identity");

            using var reconnect = await Peer.Create(version);
            await reconnect.Initialize();
            Check(reconnect.Catalog.Displays.Single().Id == first.Id, "Connection identity changed across client sessions");

            // A missing manager must be reported to the future worker supervisor, not silently freeze topology.
            await peer.Send(peer.Registry, 1, Words(10));
            try { await catalog.ReadNext(peer.Token); throw new Exception("Manager removal was ignored"); }
            catch (IOException) { }
        }
    }

    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static byte[] Words(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
    private static byte[] Text(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var bytes = new byte[4 + ((utf8.Length + 4) & ~3)];
        BitConverter.GetBytes(utf8.Length + 1).CopyTo(bytes, 0); utf8.CopyTo(bytes, 4); return bytes;
    }
    private static byte[] Global(int name, string protocol, int version) => [.. Words(name), .. Text(protocol), .. Words(version)];

    private sealed class Peer : IDisposable
    {
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        private readonly NetworkStream server;
        private readonly uint version;
        internal CancellationToken Token => timeout.Token;
        internal WaylandOutputCatalog Catalog { get; }
        internal uint Registry, Core, Logical;
        private uint manager;
        private Peer(Socket client, Socket accepted, uint version)
        {
            server = new(accepted, ownsSocket: true); Catalog = new(new WaylandWire(client)); this.version = version;
        }
        internal static async Task<Peer> Create(uint version)
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await client.ConnectAsync(listener.LocalEndPoint!);
                var accepted = await listener.AcceptAsync(); accepted.NoDelay = true;
                return new(client, accepted, version);
            }
            catch { client.Dispose(); throw; }
        }
        internal async Task Initialize()
        {
            var initializing = Catalog.Initialize(Token);
            Registry = BitConverter.ToUInt32(await Expect(1, 1));
            var callback = BitConverter.ToUInt32(await Expect(1, 0));
            await Send(Registry, 0, Global(10, "zxdg_output_manager_v1", (int)version));
            await Send(Registry, 0, Global(20, "wl_output", 3));
            await Send(callback, 0, Words(0));
            await Send(1, 1, Words((int)callback));
            manager = ReadBinding(await Expect(Registry, 0), 10, "zxdg_output_manager_v1", version);
            (Core, Logical) = await ReadOutput(20);
            callback = BitConverter.ToUInt32(await Expect(1, 0));
            await Send(Core, 1, Words(1, 1920, 1080, 60000));
            await Send(Core, 2, []); // Initial core batch precedes xdg-output properties.
            await Send(Logical, 3, Text("DP-1"));
            await Send(Logical, 0, Words(0, 0));
            await Send(Logical, 1, Words(1280, 720));
            await Send(version == 3 ? Core : Logical, 2, []);
            await Send(callback, 0, Words(0));
            await initializing;
        }
        internal async Task<(uint Core, uint Logical)> ReadOutput(uint name)
        {
            var core = ReadBinding(await Expect(Registry, 0), name, "wl_output", 3);
            var payload = await Expect(manager, 1);
            Check(payload.Length == 8 && BitConverter.ToUInt32(payload, 4) == core, "xdg-output bound to incorrect core object");
            return (core, BitConverter.ToUInt32(payload));
        }
        private static uint ReadBinding(byte[] payload, uint name, string protocol, uint version)
        {
            var reader = new WaylandReader(payload);
            Check(reader.UInt() == name && reader.Text() == protocol && reader.UInt() == version, "Registry binding mismatch");
            var id = reader.UInt(); reader.End(); return id;
        }
        internal Task Done() => Event(version == 3 ? Core : Logical, 2, []);
        internal async Task Event(uint target, ushort opcode, byte[] payload)
        {
            await Send(target, opcode, payload); await Catalog.ReadNext(Token);
        }
        internal async Task Send(uint target, ushort opcode, byte[] payload)
        {
            var header = new byte[8]; BitConverter.GetBytes(target).CopyTo(header, 0);
            BitConverter.GetBytes(((uint)(payload.Length + 8) << 16) | opcode).CopyTo(header, 4);
            // Separate writes exercise framing independently of a single contiguous packet.
            await server.WriteAsync(header.AsMemory(0, 3), Token);
            await server.WriteAsync(header.AsMemory(3), Token);
            await server.WriteAsync(payload, Token);
        }
        internal async Task<byte[]> Expect(uint target, ushort opcode)
        {
            var header = new byte[8]; await server.ReadExactlyAsync(header, Token);
            var word = BitConverter.ToUInt32(header, 4);
            Check(BitConverter.ToUInt32(header) == target && (ushort)word == opcode && word >> 16 >= 8, "Unexpected client request");
            var payload = new byte[(word >> 16) - 8]; await server.ReadExactlyAsync(payload, Token); return payload;
        }
        public void Dispose() { Catalog.Dispose(); server.Dispose(); timeout.Dispose(); }
    }
}
