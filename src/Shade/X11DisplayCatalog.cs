using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shade;

// Original XRandR 1.5 catalog, called inside the isolated X11 worker.
// Probe is a separate read-only diagnostic and must not run alongside the CupriFace host.
[SupportedOSPlatform("linux")]
internal static class X11DisplayCatalog
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Monitor
    {
        public nuint Name;
        public int Primary, Automatic, OutputCount, X, Y, Width, Height, MillimeterWidth, MillimeterHeight;
        public nint Outputs;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct OutputInfoPrefix
    {
        public nuint Timestamp, Crtc;
        public nint Name;
        public int NameLength;
    }
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport("libX11.so.6")] private static extern nint XGetAtomName(nint display, nuint atom);
    [DllImport("libX11.so.6")] private static extern nuint XGetSelectionOwner(nint display, nuint selection);
    [DllImport("libX11.so.6")] private static extern int XFree(nint data);
    [DllImport("libXrandr.so.2")] private static extern int XRRQueryVersion(nint display, out int major, out int minor);
    [DllImport("libXrandr.so.2")] private static extern nint XRRGetMonitors(nint display, nuint window, int active, out int count);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeMonitors(nint monitors);
    [DllImport("libXrandr.so.2")] private static extern nint XRRGetScreenResourcesCurrent(nint display, nuint window);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeScreenResources(nint resources);
    [DllImport("libXrandr.so.2")] private static extern nint XRRGetOutputInfo(nint display, nint resources, nuint output);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeOutputInfo(nint info);
    [DllImport("libXrandr.so.2")]
    private static extern int XRRGetOutputProperty(nint display, nuint output, nuint property, nint offset, nint length,
        int delete, int pending, nuint requestedType, out nuint actualType, out int format, out nuint count, out nuint remaining, out nint data);

    public static (IReadOnlyList<Display> Displays, bool Compositor, int RandrMajor, int RandrMinor) Probe()
    {
        var display = XOpenDisplay(0);
        if (display == 0) throw new InvalidOperationException("No accessible X11 display connection.");
        try { return Read(display, new HashSet<string>()); }
        finally { XCloseDisplay(display); }
    }

    internal static (IReadOnlyList<Display> Displays, bool Compositor, int RandrMajor, int RandrMinor) Read(nint display, ISet<string> ambiguousHardware)
    {
            if (XRRQueryVersion(display, out var major, out var minor) == 0 || major < 1 || (major == 1 && minor < 5))
                throw new NotSupportedException("XRandR 1.5 or later is required.");
            var root = XDefaultRootWindow(display);
            var selection = XInternAtom(display, "_NET_WM_CM_S" + XDefaultScreen(display), 1);
            var compositor = selection != 0 && XGetSelectionOwner(display, selection) != 0;
            var monitors = XRRGetMonitors(display, root, 1, out var count);
            nint resources = 0;
            try
            {
                if (monitors == 0 || count is < 1 or > 256) throw new InvalidOperationException("No usable XRandR monitors.");
                resources = XRRGetScreenResourcesCurrent(display, root);
                if (resources == 0) throw new InvalidOperationException("XRandR resources unavailable.");
                List<MonitorCandidate> candidates = [];
                for (var i = 0; i < count; i++)
                {
                    var monitor = Marshal.PtrToStructure<Monitor>(monitors + i * Marshal.SizeOf<Monitor>());
                    if (monitor.Width <= 0 || monitor.Height <= 0 || monitor.OutputCount is < 0 or > 256) throw new InvalidDataException("Invalid monitor bounds.");
                    var namePointer = XGetAtomName(display, monitor.Name);
                    string name;
                    try { name = Marshal.PtrToStringUTF8(namePointer) ?? "Display"; }
                    finally { if (namePointer != 0) XFree(namePointer); }
                    for (var outputIndex = 0; outputIndex < Math.Max(monitor.OutputCount, 1); outputIndex++)
                    {
                        var output = monitor.OutputCount == 0 ? 0 : (nuint)Marshal.ReadIntPtr(monitor.Outputs, outputIndex * IntPtr.Size);
                        var hardware = output == 0 ? null : ReadHardware(display, output);
                        // X resource IDs are transient. Explicit connection assignments use connector names,
                        // while trustworthy EDID identities remain independent of the connection entirely.
                        var connection = output == 0 ? "virtual|" + name : "output|" + ReadOutputName(display, resources, output);
                        candidates.Add(new(name, "x11|" + connection, name, hardware,
                            monitor.X, monitor.Y, monitor.Width, monitor.Height));
                    }
                }
                return (MonitorIdentity.Resolve(candidates, ambiguousHardware), compositor, major, minor);
            }
            finally
            {
                if (resources != 0) XRRFreeScreenResources(resources);
                if (monitors != 0) XRRFreeMonitors(monitors);
            }
    }
    private static string ReadOutputName(nint display, nint resources, nuint output)
    {
        var pointer = XRRGetOutputInfo(display, resources, output);
        if (pointer == 0) throw new InvalidOperationException("XRandR output disappeared.");
        try
        {
            var info = Marshal.PtrToStructure<OutputInfoPrefix>(pointer);
            if (info.Name == 0 || info.NameLength is < 1 or > 4096) throw new InvalidDataException("Invalid XRandR connector name.");
            return Marshal.PtrToStringUTF8(info.Name, info.NameLength)!;
        }
        finally { XRRFreeOutputInfo(pointer); }
    }
    private static string? ReadHardware(nint display, nuint output)
    {
        var atom = XInternAtom(display, "EDID", 1);
        if (atom == 0) return null;
        var result = XRRGetOutputProperty(display, output, atom, 0, 64, 0, 0, 0,
            out _, out var format, out var count, out _, out var data);
        try
        {
            if (result != 0 || format != 8 || count is < 128 or > 256 || data == 0) return null;
            var edid = new byte[(int)count]; Marshal.Copy(data, edid, 0, edid.Length);
            return MonitorIdentity.HardwareKey(edid);
        }
        finally { if (data != 0) XFree(data); }
    }
}
