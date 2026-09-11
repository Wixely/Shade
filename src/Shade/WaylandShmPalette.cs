using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Shade;

// Immutable 1x1 premultiplied black pixels. Compositor viewports scale them to each output.
// A shared palette avoids per-screen/per-frame pixel uploads and never overwrites an in-use buffer.
internal sealed class WaylandShmPalette(WaylandWire wire, uint shm)
{
    private readonly Dictionary<int, uint> buffers = [];
    private uint pool;
    internal async Task Initialize(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Wayland shared memory currently requires 64-bit Linux.");
        if (pool != 0) throw new InvalidOperationException("Palette already initialized.");
        var descriptor = memfd_create("shade-palette", 3); // CLOEXEC | ALLOW_SEALING
        if (descriptor < 0) throw new IOException("Could not allocate Wayland shared memory.");
        using var file = new SafeFileHandle(descriptor, ownsHandle: true);
        var pixels = new byte[80 * 4];
        for (var level = 1; level <= 80; level++)
            BitConverter.TryWriteBytes(pixels.AsSpan((level - 1) * 4), (uint)DimLevel.Alpha(level) << 24);
        RandomAccess.SetLength(file, pixels.Length);
        RandomAccess.Write(file, pixels, 0);
        // Seal size, not writes: libwayland-server maps SHM with PROT_READ | PROT_WRITE.
        // Shade closes its descriptor after transfer and never changes these pixel contents.
        if (fcntl(file, 1033, 7) < 0) throw new IOException("Could not seal Wayland shared memory.");
        pool = wire.Allocate();
        await wire.SendDescriptor(shm, 0, WaylandWire.Words(pool, (uint)pixels.Length), file, token);
    }
    internal async Task<uint> GetBuffer(int level, CancellationToken token)
    {
        DimLevel.Validate(level);
        if (level == 0) throw new ArgumentOutOfRangeException(nameof(level), "Zero dimming uses an unmapped surface.");
        if (pool == 0) throw new InvalidOperationException("Palette is not initialized.");
        if (buffers.TryGetValue(level, out var existing)) return existing;
        var buffer = wire.Allocate();
        // wl_shm_pool.create_buffer: id, offset, width, height, stride, ARGB8888
        await wire.Send(pool, 0, WaylandWire.Words(buffer, (uint)((level - 1) * 4), 1, 1, 4, 0), token);
        buffers.Add(level, buffer); return buffer;
    }
    internal async Task Destroy(CancellationToken token)
    {
        foreach (var buffer in buffers.Values) await wire.Send(buffer, 0, [], token);
        buffers.Clear();
        if (pool != 0) { await wire.Send(pool, 1, [], token); pool = 0; }
    }
    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle descriptor, int command, int argument);
}
