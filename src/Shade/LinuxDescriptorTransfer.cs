using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Shade;

// Linux 64-bit ABI. Sends one SCM_RIGHTS descriptor alongside the first bytes only.
internal static class LinuxDescriptorTransfer
{
    internal static async Task<int> Send(Socket socket, byte[] message, SafeFileHandle descriptor, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Wayland descriptor transfer currently requires 64-bit Linux.");
        var pin = GCHandle.Alloc(message, GCHandleType.Pinned);
        var native = Marshal.AllocHGlobal(96); // iovec 16, cmsghdr+int aligned to 24, msghdr 56
        var held = false;
        try
        {
            descriptor.DangerousAddRef(ref held);
            Marshal.Copy(new byte[96], 0, native, 96);
            Marshal.WriteIntPtr(native, pin.AddrOfPinnedObject()); Marshal.WriteInt64(native, 8, message.Length);
            var control = native + 16; var header = native + 40;
            Marshal.WriteInt64(control, 20); // CMSG_LEN(sizeof(int))
            Marshal.WriteInt32(control, 8, 1); Marshal.WriteInt32(control, 12, 1); // SOL_SOCKET, SCM_RIGHTS
            Marshal.WriteInt32(control, 16, descriptor.DangerousGetHandle().ToInt32());
            Marshal.WriteIntPtr(header, 16, native); Marshal.WriteInt64(header, 24, 1);
            Marshal.WriteIntPtr(header, 32, control); Marshal.WriteInt64(header, 40, 24);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var sent = sendmsg(socket.SafeHandle, header, 0x4000 | 0x40); // MSG_NOSIGNAL | MSG_DONTWAIT
                if (sent > 0) return checked((int)sent);
                var error = Marshal.GetLastPInvokeError();
                if (sent < 0 && error == 4) continue; // EINTR, no bytes transferred
                if (sent < 0 && error == 11) { await Task.Delay(1, token); continue; } // EAGAIN, only under backpressure
                throw new IOException("Wayland descriptor transfer failed.");
            }
        }
        finally
        {
            if (held) descriptor.DangerousRelease();
            Marshal.FreeHGlobal(native); pin.Free();
        }
    }
    [DllImport("libc", SetLastError = true)]
    private static extern nint sendmsg(SafeSocketHandle socket, nint message, int flags);
}
