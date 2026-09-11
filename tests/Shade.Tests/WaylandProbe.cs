using System.Net.Sockets;
using System.Text;

// Read-only protocol capability investigation. Creates only registry/callback objects;
// it never binds output/surface/input globals or changes compositor configuration.
internal static class WaylandProbe
{
    public static async Task Run()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Wayland probing requires Linux.");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_SOCKET")))
            throw new NotSupportedException("This read-only probe does not consume inherited Wayland descriptors.");
        var display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
        var path = Path.IsPathRooted(display) ? display : Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? throw new InvalidOperationException("No Wayland runtime directory."), display);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), lifetime.Token);
        using var stream = new NetworkStream(socket);
        var requests = new byte[24];
        // Wire words use native byte order. Object 1 is wl_display. Allocate IDs densely.
        Write(requests, 0, 1); Write(requests, 4, (12u << 16) | 1); Write(requests, 8, 2); // get_registry
        Write(requests, 12, 1); Write(requests, 16, 12u << 16); Write(requests, 20, 3); // sync
        await stream.WriteAsync(requests, lifetime.Token);
        var globals = new Dictionary<uint, (string Name, uint Version)>();
        var header = new byte[8];
        var messages = 0;
        while (++messages <= 4096)
        {
            await stream.ReadExactlyAsync(header, lifetime.Token);
            var sender = BitConverter.ToUInt32(header, 0);
            var word = BitConverter.ToUInt32(header, 4);
            var size = (int)(word >> 16); var opcode = word & 65535;
            if (size < 8 || size % 4 != 0) throw new InvalidDataException("Invalid Wayland event size.");
            var payload = new byte[size - 8];
            await stream.ReadExactlyAsync(payload, lifetime.Token);
            if (sender == 1 && opcode == 0) throw new InvalidDataException("Compositor rejected registry probe.");
            if (sender == 2 && opcode == 0)
            {
                if (payload.Length < 12) throw new InvalidDataException("Incomplete registry global.");
                var name = BitConverter.ToUInt32(payload, 0); var length = BitConverter.ToUInt32(payload, 4);
                if (length is < 2 or > 256) throw new InvalidDataException("Invalid interface name length.");
                var padded = ((int)length + 3) & ~3;
                if (payload.Length != 12 + padded || payload[7 + (int)length] != 0)
                    throw new InvalidDataException("Malformed registry global.");
                var text = new UTF8Encoding(false, true).GetString(payload, 8, (int)length - 1);
                if (text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) throw new InvalidDataException("Invalid interface name.");
                globals[name] = (text, BitConverter.ToUInt32(payload, 8 + padded));
            }
            else if (sender == 2 && opcode == 1)
            {
                if (payload.Length != 4) throw new InvalidDataException("Malformed global removal.");
                globals.Remove(BitConverter.ToUInt32(payload));
            }
            else if (sender == 3 && opcode == 0)
            {
                if (payload.Length != 4) throw new InvalidDataException("Malformed sync callback.");
                foreach (var group in globals.Values.GroupBy(g => g.Name).OrderBy(g => g.Key))
                    Console.WriteLine($"{group.Key}: version {group.Max(g => g.Version)}, count {group.Count()}");
                Console.WriteLine("Registry only; no surfaces created. Protocol presence is not overlay acceptance.");
                return;
            }
            else if (sender != 1) throw new InvalidDataException("Unexpected registry probe event.");
        }
        throw new InvalidDataException("Wayland registry event limit exceeded.");
    }
    private static void Write(byte[] bytes, int offset, uint value) => BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
}
