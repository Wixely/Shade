using System.Net.Sockets;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Shade;

// Single-reader/single-writer transport. Outgoing SHM descriptors are supported;
// do not bind objects with incoming FD events until recvmsg support is implemented.
internal sealed class WaylandWire : IDisposable
{
    private readonly Socket socket;
    private readonly NetworkStream stream;
    private uint nextId = 2;
    private readonly object idGate = new();
    private readonly HashSet<uint> allocated = [];
    private readonly Queue<uint> released = [];
    internal WaylandWire(Socket socket) { this.socket = socket; stream = new(socket); }
    internal static async Task<WaylandWire> Connect(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Wayland requires Linux.");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_SOCKET")))
            throw new NotSupportedException("Inherited Wayland descriptors are not supported yet.");
        var display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
        var path = Path.IsPathRooted(display) ? display : Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? throw new InvalidOperationException("No Wayland runtime directory."), display);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token); return new(socket); }
        catch { socket.Dispose(); throw; }
    }
    internal uint Allocate()
    {
        lock (idGate)
        {
            var id = released.TryDequeue(out var reusable) ? reusable : nextId < 0xff000000 ? nextId++
                : throw new InvalidOperationException("Wayland object IDs exhausted.");
            if (!allocated.Add(id)) throw new InvalidOperationException("Duplicate Wayland object allocation.");
            return id;
        }
    }
    internal async Task Send(uint target, ushort opcode, byte[] payload, CancellationToken token)
        => await stream.WriteAsync(Frame(target, opcode, payload), token);
    internal async Task SendDescriptor(uint target, ushort opcode, byte[] payload, SafeFileHandle descriptor, CancellationToken token)
    {
        var message = Frame(target, opcode, payload);
        var sent = await LinuxDescriptorTransfer.Send(socket, message, descriptor, token);
        // A positive partial send transfers the descriptor. Never send it a second time.
        if (sent < message.Length) await stream.WriteAsync(message.AsMemory(sent), token);
    }
    private static byte[] Frame(uint target, ushort opcode, byte[] payload)
    {
        var length = checked(payload.Length + 8);
        if (length > 65532 || length % 4 != 0) throw new ArgumentException("Invalid Wayland message size.");
        var message = new byte[length];
        BitConverter.TryWriteBytes(message.AsSpan(), target);
        BitConverter.TryWriteBytes(message.AsSpan(4), ((uint)length << 16) | opcode);
        payload.CopyTo(message, 8);
        return message;
    }
    internal async Task<WaylandMessage> Read(CancellationToken token)
    {
        var header = new byte[8]; await stream.ReadExactlyAsync(header, token);
        var target = BitConverter.ToUInt32(header); var word = BitConverter.ToUInt32(header, 4);
        var length = (int)(word >> 16);
        if (length < 8 || length % 4 != 0) throw new InvalidDataException("Invalid Wayland event size.");
        var payload = new byte[length - 8]; await stream.ReadExactlyAsync(payload, token);
        if (target == 1 && (word & 65535) == 0) throw new InvalidDataException("Compositor reported a protocol error.");
        if (target == 1 && (word & 65535) == 1)
        {
            if (payload.Length != 4) throw new InvalidDataException("Invalid Wayland deletion acknowledgment.");
            var id = BitConverter.ToUInt32(payload);
            // The background reader may receive acknowledgments while the actor allocates objects.
            lock (idGate)
            {
                if (!allocated.Remove(id)) throw new InvalidDataException("Unknown Wayland object deletion acknowledgment.");
                released.Enqueue(id);
            }
        }
        return new(target, (ushort)word, payload);
    }
    internal async Task RoundTrip(Func<WaylandMessage, CancellationToken, Task> handle, CancellationToken token)
    {
        var callback = Allocate(); await Send(1, 0, Words(callback), token);
        for (var count = 0; count < 16384; count++)
        {
            var message = await Read(token);
            if (message.Target == callback && message.Opcode == 0 && message.Payload.Length == 4) return;
            await handle(message, token);
        }
        throw new InvalidDataException("Wayland round trip event limit exceeded.");
    }
    internal static byte[] Words(params uint[] words)
    {
        var bytes = new byte[checked(words.Length * 4)];
        for (var i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }
    internal static byte[] Binding(uint name, string protocol, uint version, uint id)
    {
        var text = Encoding.UTF8.GetBytes(protocol); var padded = (text.Length + 4) & ~3;
        var bytes = new byte[16 + padded];
        Words(name, (uint)text.Length + 1).CopyTo(bytes, 0); text.CopyTo(bytes, 8);
        Words(version, id).CopyTo(bytes, 8 + padded); return bytes;
    }
    public void Dispose() { stream.Dispose(); socket.Dispose(); }
}
internal sealed record WaylandMessage(uint Target, ushort Opcode, byte[] Payload);
internal ref struct WaylandReader(ReadOnlySpan<byte> bytes)
{
    private ReadOnlySpan<byte> remaining = bytes;
    internal uint UInt()
    {
        if (remaining.Length < 4) throw new InvalidDataException("Truncated Wayland event.");
        var value = BitConverter.ToUInt32(remaining); remaining = remaining[4..]; return value;
    }
    internal int Int() => unchecked((int)UInt());
    internal string Text()
    {
        var length = UInt();
        if (length is < 1 or > 4096) throw new InvalidDataException("Invalid Wayland string length.");
        var padded = ((int)length + 3) & ~3;
        if (remaining.Length < padded || remaining[(int)length - 1] != 0 || remaining[..((int)length - 1)].Contains((byte)0))
            throw new InvalidDataException("Malformed Wayland string.");
        var text = new UTF8Encoding(false, true).GetString(remaining[..((int)length - 1)]);
        remaining = remaining[padded..]; return text;
    }
    internal void End() { if (!remaining.IsEmpty) throw new InvalidDataException("Unexpected Wayland event arguments."); }
}
