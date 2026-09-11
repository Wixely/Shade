using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using static Shade.WindowsNative;

namespace Shade;

[SupportedOSPlatform("windows")]
internal static class WindowsDisplayCatalog
{
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Source { public Luid Adapter; public uint Id, Mode, Status; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Target
    {
        public Luid Adapter; public uint Id, Mode, Technology, Rotation, Scaling, RateNumerator, RateDenominator, ScanLine, Available, Status;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public Source Source; public Target Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public Header Header; public uint Flags, Technology; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, nint modes, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetSourceName(ref SourceName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetTargetName(ref TargetName name);

    public static IReadOnlyList<Display> Read(ISet<string> ambiguousHardware)
    {
        var paths = ReadPaths();
        var monitors = new Dictionary<string, Rect>(StringComparer.OrdinalIgnoreCase);
        var error = 0;
        MonitorCallback callback = (nint monitor, nint dc, ref Rect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfoW(monitor, ref info)) { error = Marshal.GetLastWin32Error(); return false; }
            monitors[info.Device] = info.Monitor;
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception(error);
        List<MonitorCandidate> candidates = [];
        foreach (var path in paths)
        {
            var source = new SourceName { Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = path.Source.Adapter, Id = path.Source.Id }, Name = "" };
            var target = new TargetName { Header = new() { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(), Adapter = path.Target.Adapter, Id = path.Target.Id }, FriendlyName = "", DevicePath = "" };
            Check(GetSourceName(ref source));
            Check(GetTargetName(ref target));
            if (!monitors.TryGetValue(source.Name, out var bounds)) throw new InvalidOperationException("Display topology is changing.");
            var connection = target.DevicePath;
            if (string.IsNullOrWhiteSpace(connection))
                connection = $"session|{path.Target.Adapter.High}|{path.Target.Adapter.Low}|{path.Target.Id}";
            var hardware = ReadHardwareKey(target.DevicePath);
            candidates.Add(new(source.Name, connection, string.IsNullOrWhiteSpace(target.FriendlyName) ? "Display" : target.FriendlyName,
                hardware, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
        }
        if (monitors.Count != candidates.Select(c => c.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidOperationException("Display topology snapshots disagree.");
        return MonitorIdentity.Resolve(candidates, ambiguousHardware);
    }

    private static PathInfo[] ReadPaths()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(2, out var pathCount, out var modeCount));
            if (pathCount > 256 || modeCount > 1024) throw new InvalidOperationException("Unexpected display count.");
            var paths = new PathInfo[pathCount];
            var modes = Marshal.AllocHGlobal(checked((int)Math.Max(modeCount, 1) * 64));
            try
            {
                var result = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0);
                if (result == 122) continue;
                Check(result);
                return paths[..(int)pathCount];
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        throw new InvalidOperationException("Display topology did not settle.");
    }
    private static void Check(int result) { if (result != 0) throw new Win32Exception(result); }

    private static string? ReadHardwareKey(string devicePath)
    {
        // Device-interface paths identify the PnP registry instance. Raw values never enter logs/settings.
        var parts = devicePath.Split('#');
        if (parts.Length != 4 || !parts[0].Equals(@"\\?\DISPLAY", StringComparison.OrdinalIgnoreCase) ||
            parts[1].IndexOfAny(['\\', '/']) >= 0 || parts[2].IndexOfAny(['\\', '/']) >= 0) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            return key?.GetValue("EDID") is byte[] bytes ? MonitorIdentity.HardwareKey(bytes) : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
}
