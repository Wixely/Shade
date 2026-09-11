using System.Net;
using System.Net.Sockets;
using Shade;

internal static class WaylandLayerTests
{
    internal static async Task Run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await client.ConnectAsync(listener.LocalEndPoint!, token);
        using var server = await listener.AcceptAsync(token);
        server.NoDelay = true;
        using var stream = new NetworkStream(server);
        using var wire = new WaylandWire(client);
        var bufferReads = 0;
        var surfaces = new WaylandLayerSurfaces(wire, 100, 101, 102, (level, _) =>
        {
            bufferReads++; return Task.FromResult((uint)(1000 + level));
        });
        var first = new WaylandOverlayTarget(new("one", "Screen", -1280, 0, 1280, 720), 200, 30);
        var disabled = new WaylandOverlayTarget(new("two", "Screen", 0, 0, 1920, 1080), 201, 0);
        await surfaces.Reconcile([first, disabled], token);
        var ids = await Created(200);
        Check(bufferReads == 0 && surfaces.Count == 1, "Attached pixels before configure or enabled a disabled output");
        await Barrier();

        await Configure(ids.Layer, 700, 1280, 720);
        await Committed(ids, 700, 1280, 720, 1030);
        await surfaces.Reconcile([first, disabled], token);
        await Barrier();
        Check(bufferReads == 1, "Unchanged level acquired another buffer");

        // Configure dimensions come from the compositor, independently of catalog update ordering.
        await Configure(ids.Layer, 701, 720, 1280);
        await Committed(ids, 701, 720, 1280, 1030);
        first = first with { Level = 45 };
        await surfaces.Reconcile([first, disabled], token);
        await Committed(ids, null, 720, 1280, 1045);
        await Barrier();

        try
        {
            await surfaces.Reconcile([first with { Level = 0 }, disabled with { Level = 81 }], token);
            throw new Exception("Invalid batch was accepted");
        }
        catch (ArgumentOutOfRangeException) { }
        Check(surfaces.Count == 1, "Invalid batch partially removed a surface");
        await Barrier();

        await surfaces.Reconcile([first with { Level = 0 }, disabled], token);
        await Destroyed(ids);
        Check(!await surfaces.Handle(new(ids.Layer, 0, WaylandWire.Words(702, 800, 600)), token), "Retired configure recreated an overlay");
        await Barrier();
        await surfaces.Reconcile([first, disabled], token);
        var replacement = await Created(200);
        Check(replacement.Surface != ids.Surface, "Re-enable reused a retired surface");
        await Configure(replacement.Layer, 703, 0, 0);
        await Committed(replacement, 703, 1280, 720, 1045);
        try
        {
            await surfaces.Handle(new(replacement.Layer, 1, []), token);
            throw new Exception("Compositor close was not reported");
        }
        catch (IOException) { }
        await Destroyed(replacement);
        Check(surfaces.Count == 0, "Compositor close left shading surfaces alive");
        await Barrier();

        var reusable = wire.Allocate();
        var beforeAcknowledgment = wire.Allocate();
        Check(beforeAcknowledgment != reusable, "Object ID reused before server acknowledgment");
        for (var cycle = 0; cycle < 100; cycle++)
        {
            await Delete(reusable);
            await wire.Read(token);
            Check(wire.Allocate() == reusable, "Acknowledged IDs grew instead of being reused");
        }
        await Delete(reusable); await wire.Read(token);
        await Delete(reusable);
        try { await wire.Read(token); throw new Exception("Duplicate deletion acknowledgment was accepted"); }
        catch (InvalidDataException) { }

        async Task Delete(uint id)
        {
            byte[] message = [.. BitConverter.GetBytes(1u), .. BitConverter.GetBytes((12u << 16) | 1), .. BitConverter.GetBytes(id)];
            await stream.WriteAsync(message, token);
        }

        async Task Configure(uint layer, uint serial, uint width, uint height)
            => Check(await surfaces.Handle(new(layer, 0, WaylandWire.Words(serial, width, height)), token), "Configure was not handled");
        async Task<(uint Surface, uint Layer, uint Viewport)> Created(uint output)
        {
            var surface = BitConverter.ToUInt32(await Read(100, 0));
            var payload = await Read(101, 0);
            var reader = new WaylandReader(payload);
            var layer = reader.UInt();
            Check(reader.UInt() == surface && reader.UInt() == output && reader.UInt() == 3 && reader.Text() == "shade", "Wrong layer, output or namespace");
            reader.End();
            payload = await Read(102, 1);
            var viewport = BitConverter.ToUInt32(payload);
            Check(BitConverter.ToUInt32(payload, 4) == surface, "Viewport attached to wrong surface");
            var empty = BitConverter.ToUInt32(await Read(100, 1));
            await Expect(surface, 5, empty);
            await Expect(empty, 0);
            await Expect(layer, 0, 0, 0);
            await Expect(layer, 1, 15);
            await Expect(layer, 2, uint.MaxValue);
            await Expect(layer, 4, 0);
            await Expect(surface, 6);
            return (surface, layer, viewport);
        }
        async Task Committed((uint Surface, uint Layer, uint Viewport) ids, uint? serial, uint width, uint height, uint buffer)
        {
            if (serial.HasValue) await Expect(ids.Layer, 6, serial.Value);
            await Expect(ids.Viewport, 2, width, height);
            await Expect(ids.Surface, 1, buffer, 0, 0);
            await Expect(ids.Surface, 9, 0, 0, 1, 1);
            await Expect(ids.Surface, 6);
        }
        async Task Destroyed((uint Surface, uint Layer, uint Viewport) ids)
        {
            await Expect(ids.Viewport, 0); await Expect(ids.Layer, 7); await Expect(ids.Surface, 0);
        }
        async Task Barrier()
        {
            // Any unexpected frame request, redraw or other output precedes this marker and fails Read.
            await wire.Send(999, 0, [], token); await Expect(999, 0);
        }
        async Task Expect(uint target, ushort opcode, params uint[] values)
            => Check((await Read(target, opcode)).SequenceEqual(WaylandWire.Words(values)), "Unexpected request arguments");
        async Task<byte[]> Read(uint target, ushort opcode)
        {
            var header = new byte[8]; await stream.ReadExactlyAsync(header, token);
            var word = BitConverter.ToUInt32(header, 4);
            Check(BitConverter.ToUInt32(header) == target && (ushort)word == opcode && word >> 16 >= 8, "Unexpected surface request order");
            var bytes = new byte[(word >> 16) - 8]; await stream.ReadExactlyAsync(bytes, token); return bytes;
        }
    }
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
}
