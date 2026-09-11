using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Shade;

// Protocol fixture, not a renderer. SHM descriptor transfer is covered separately against WSLg.
internal sealed class WaylandCompositorFixture : IAsyncDisposable
{
    internal sealed record Output(string Name, int X, int Y, int Width, int Height);
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, Client> clients = new();
    private readonly ConcurrentQueue<string> errors = new();
    private IReadOnlyDictionary<uint, Output> outputs = new Dictionary<uint, Output>
    { [20] = new("DP-1", 0, 0, 1280, 720), [21] = new("DP-2", 1280, 0, 1920, 1080) };
    private readonly Task accepting;
    private long requestCount;
    internal long RequestCount => Interlocked.Read(ref requestCount);
    internal string SocketPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shade-" + Guid.NewGuid().ToString("N") + ".sock");
    internal WaylandCompositorFixture()
    {
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath)); listener.Listen(8); accepting = Accept();
    }
    internal Dictionary<uint, int> Mapped => clients.Values.SelectMany(c => c.Mapped()).ToDictionary(p => p.Key, p => p.Value);
    internal void CheckErrors() { if (errors.TryPeek(out var error)) throw new Exception("Compositor fixture: " + error); }
    internal void DisconnectClients() { foreach (var client in clients.Values) client.Dispose(); }
    internal async Task Change(uint name, Output? value)
    {
        var next = new Dictionary<uint, Output>(outputs); var existed = next.ContainsKey(name);
        if (value is null) next.Remove(name); else next[name] = value;
        Volatile.Write(ref outputs, next);
        foreach (var client in clients.Values) await client.Change(name, value, existed);
    }
    private async Task Accept()
    {
        var sequence = 0;
        try
        {
            while (true)
            {
                var socket = await listener.AcceptAsync(lifetime.Token);
                var id = ++sequence; var client = new Client(this, socket); clients[id] = client;
                client.Completion = Run();
                async Task Run()
                {
                    try { await client.Run(); }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException) { }
                    catch (Exception ex) { errors.Enqueue(ex.Message); }
                    finally { clients.TryRemove(id, out _); client.Dispose(); }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Dispose(); await accepting;
        var closing = clients.Values.ToArray(); foreach (var client in closing) client.Dispose();
        await Task.WhenAll(closing.Select(c => c.Completion));
        lifetime.Dispose(); File.Delete(SocketPath);
    }
    private static byte[] Words(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
    private static byte[] Text(string value)
    {
        var text = Encoding.UTF8.GetBytes(value); var bytes = new byte[4 + ((text.Length + 4) & ~3)];
        BitConverter.GetBytes(text.Length + 1).CopyTo(bytes, 0); text.CopyTo(bytes, 4); return bytes;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Surface
    {
        internal uint Layer, Output, Buffer, Viewport, Serial;
        internal bool InputEmpty, Configured, Acknowledged, Mapped;
        internal uint Anchors, Keyboard = uint.MaxValue;
        internal int Width, Height;
    }
    private sealed class Client(WaylandCompositorFixture owner, Socket socket) : IDisposable
    {
        private readonly NetworkStream stream = new(socket, ownsSocket: true);
        private readonly SemaphoreSlim writing = new(1);
        private readonly object gate = new();
        private readonly Dictionary<uint, string> objects = new() { [1] = "display" };
        private readonly Dictionary<uint, uint> coreOutputs = [], logicalOutputs = [];
        private readonly Dictionary<uint, int> buffers = [];
        private readonly Dictionary<uint, Surface> surfaces = [];
        private uint registry, serial;
        internal Task Completion = Task.CompletedTask;
        internal Dictionary<uint, int> Mapped()
        { lock (gate) return surfaces.Values.Where(s => s.Mapped).ToDictionary(s => s.Output, s => buffers[s.Buffer]); }
        internal async Task Run()
        {
            while (true)
            {
                var header = new byte[8]; await stream.ReadExactlyAsync(header, owner.lifetime.Token);
                var target = BitConverter.ToUInt32(header); var word = BitConverter.ToUInt32(header, 4);
                Check(word >> 16 >= 8, "Invalid request size");
                var payload = new byte[(word >> 16) - 8]; await stream.ReadExactlyAsync(payload, owner.lifetime.Token);
                Interlocked.Increment(ref owner.requestCount);
                List<WaylandMessage> replies;
                lock (gate) replies = Handle(target, (ushort)word, payload);
                await Send(replies);
            }
        }
        private List<WaylandMessage> Handle(uint target, ushort opcode, byte[] bytes)
        {
            var replies = new List<WaylandMessage>(); var r = new WaylandReader(bytes);
            var kind = objects[target];
            if (kind == "display")
            {
                var id = r.UInt();
                if (opcode == 0) { replies.Add(new(id, 0, Words(0))); replies.Add(new(1, 1, Words((int)id))); }
                else
                {
                    registry = id; objects[id] = "registry";
                    foreach (var global in new[] { (10, "zxdg_output_manager_v1", 3), (11, "wl_compositor", 4), (12, "zwlr_layer_shell_v1", 1), (13, "wp_viewporter", 1), (14, "wl_shm", 1) })
                        replies.Add(Global(global.Item1, global.Item2, global.Item3));
                    foreach (var output in owner.outputs.Keys) replies.Add(Global((int)output, "wl_output", 3));
                }
            }
            else if (kind == "registry")
            {
                var name = r.UInt(); var protocol = r.Text(); _ = r.UInt(); var id = r.UInt(); objects[id] = protocol;
                if (protocol == "wl_output") { coreOutputs[id] = name; replies.Add(new(id, 2, [])); }
            }
            else if (kind == "zxdg_output_manager_v1")
            {
                var id = r.UInt(); var core = r.UInt(); objects[id] = "xdg_output"; logicalOutputs[id] = core;
                var output = owner.outputs[coreOutputs[core]];
                replies.Add(new(id, 3, Text(output.Name))); Geometry(replies, id, core, output);
            }
            else if (kind == "wl_shm") { var id = r.UInt(); Check(r.UInt() == 320, "Unexpected palette size"); objects[id] = "pool"; }
            else if (kind == "pool" && opcode == 0)
            {
                var id = r.UInt(); var offset = r.UInt(); Check(offset % 4 == 0 && offset < 320, "Invalid palette offset");
                Check(r.UInt() == 1 && r.UInt() == 1 && r.UInt() == 4 && r.UInt() == 0, "Invalid palette buffer");
                objects[id] = "buffer"; buffers[id] = (int)offset / 4 + 1;
            }
            else if (kind == "wl_compositor")
            {
                var id = r.UInt(); objects[id] = opcode == 0 ? "surface" : "region";
                if (opcode == 0) surfaces[id] = new();
            }
            else if (kind == "zwlr_layer_shell_v1")
            {
                var id = r.UInt(); var surface = r.UInt(); var output = r.UInt();
                Check(r.UInt() == 3 && r.Text() == "shade", "Wrong layer role");
                objects[id] = "layer"; surfaces[surface].Layer = id; surfaces[surface].Output = coreOutputs[output];
            }
            else if (kind == "wp_viewporter") { var id = r.UInt(); var surface = r.UInt(); objects[id] = "viewport"; surfaces[surface].Viewport = id; }
            else if (kind == "layer")
            {
                var surface = surfaces.Values.Single(s => s.Layer == target);
                switch (opcode)
                {
                    case 0: Check(r.UInt() == 0 && r.UInt() == 0, "Not compositor-sized"); break;
                    case 1: surface.Anchors = r.UInt(); break;
                    case 2: Check(r.UInt() == uint.MaxValue, "Reserved desktop space"); break;
                    case 4: surface.Keyboard = r.UInt(); break;
                    case 6: Check(r.UInt() == surface.Serial, "Wrong configure serial"); surface.Acknowledged = true; break;
                    case 7: break;
                    default: throw new Exception("Unexpected layer request");
                }
            }
            else if (kind == "viewport" && opcode == 2)
            { var surface = surfaces.Values.Single(s => s.Viewport == target); surface.Width = r.Int(); surface.Height = r.Int(); }
            else if (kind == "surface")
            {
                var surface = surfaces[target];
                switch (opcode)
                {
                    case 0: surfaces.Remove(target); break;
                    case 1: surface.Buffer = r.UInt(); Check(r.Int() == 0 && r.Int() == 0, "Nonzero buffer offset"); break;
                    case 5: surface.InputEmpty = objects[r.UInt()] == "region"; break;
                    case 9: Check(r.UInt() == 0 && r.UInt() == 0 && r.UInt() == 1 && r.UInt() == 1, "Unexpected damage"); break;
                    case 6:
                        Check(surface.InputEmpty && surface.Anchors == 15 && surface.Keyboard == 0, "Interactive overlay or incomplete anchors");
                        if (!surface.Configured) { Check(surface.Buffer == 0, "Mapped before configure"); Configure(replies, surface); }
                        else
                        {
                            Check(surface.Acknowledged && buffers.ContainsKey(surface.Buffer), "Commit before acknowledgment or without buffer");
                            var output = owner.outputs[surface.Output];
                            Check(surface.Width == output.Width && surface.Height == output.Height, "Viewport geometry mismatch"); surface.Mapped = true;
                        }
                        break;
                    default: throw new Exception("Unexpected surface request (including frame callbacks)");
                }
            }
            r.End();
            if ((opcode == 0 && kind is "region" or "viewport" or "buffer" or "wl_output" or "xdg_output" or "surface")
                || (kind == "layer" && opcode == 7) || (kind == "pool" && opcode == 1))
            {
                objects.Remove(target); coreOutputs.Remove(target); logicalOutputs.Remove(target); buffers.Remove(target);
                replies.Add(new(1, 1, Words((int)target)));
            }
            return replies;
        }
        private WaylandMessage Global(int id, string protocol, int version) => new(registry, 0, [.. Words(id), .. Text(protocol), .. Words(version)]);
        private static void Geometry(List<WaylandMessage> messages, uint logical, uint core, Output output)
        { messages.Add(new(logical, 0, Words(output.X, output.Y))); messages.Add(new(logical, 1, Words(output.Width, output.Height))); messages.Add(new(core, 2, [])); }
        private void Configure(List<WaylandMessage> messages, Surface surface)
        {
            var output = owner.outputs[surface.Output]; surface.Configured = true; surface.Acknowledged = false; surface.Serial = ++serial;
            messages.Add(new(surface.Layer, 0, Words((int)surface.Serial, output.Width, output.Height)));
        }
        internal async Task Change(uint name, Output? value, bool existed)
        {
            var messages = new List<WaylandMessage>();
            lock (gate)
            {
                if (registry == 0) return;
                if (value is null) messages.Add(new(registry, 1, Words((int)name)));
                else if (!existed) messages.Add(Global((int)name, "wl_output", 3));
                else
                {
                    foreach (var pair in logicalOutputs.Where(p => coreOutputs[p.Value] == name)) Geometry(messages, pair.Key, pair.Value, value);
                    foreach (var surface in surfaces.Values.Where(s => s.Output == name)) Configure(messages, surface);
                }
            }
            await Send(messages);
        }
        private async Task Send(IEnumerable<WaylandMessage> messages)
        {
            await writing.WaitAsync(owner.lifetime.Token);
            try
            {
                foreach (var message in messages)
                {
                    var header = new byte[8]; BitConverter.GetBytes(message.Target).CopyTo(header, 0);
                    BitConverter.GetBytes(((uint)(message.Payload.Length + 8) << 16) | message.Opcode).CopyTo(header, 4);
                    await stream.WriteAsync(header, owner.lifetime.Token); await stream.WriteAsync(message.Payload, owner.lifetime.Token);
                }
            }
            finally { writing.Release(); }
        }
        public void Dispose() => stream.Dispose();
    }
}
