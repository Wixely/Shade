using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Shade;

// Raising the existing window is what a second launch is for. The running instance does it to its
// own window, after the newly launched process has handed over its foreground right, because
// Windows only lets the process that the user just interacted with give focus away.
[SupportedOSPlatform("windows")]
internal static class WindowActivation
{
    private const int Hide = 0, Show = 5, Restore = 9;
    private const int OwnerWindow = 4;

    // Called by the launched duplicate before it forwards, naming the instance that should come
    // forward. Failure is not an error: the window is still shown, just possibly behind others.
    internal static void AllowForeground(int process)
    {
        try { _ = Native.AllowSetForegroundWindow(process); }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException) { }
    }

    internal static bool Raise(string title)
    {
        var window = Find(title);
        if (window == 0) return false;
        _ = Native.ShowWindow(window, Native.IsIconic(window) ? Restore : Show);
        _ = Native.SetForegroundWindow(window);
        return true;
    }

    internal static bool Conceal(string title)
    {
        var window = Find(title);
        if (window == 0) return false;
        return Native.ShowWindow(window, Hide);
    }

    // The control window is this process's own unowned top-level window carrying the application
    // title. Overlay and controller windows are excluded by class and by title, so a forwarded
    // request can never raise or hide a dimming overlay.
    private static nint Find(string title)
    {
        var found = nint.Zero;
        var process = Environment.ProcessId;
        bool Visit(nint window, nint _)
        {
            if (!Native.GetWindowThreadProcessId(window, out var owner) || owner != process) return true;
            if (Native.GetWindow(window, OwnerWindow) != 0) return true;
            if (Text(window, Native.GetWindowTextW) != title) return true;
            if (Text(window, Native.GetClassNameW).StartsWith("Shade.Overlay.", StringComparison.Ordinal)) return true;
            found = window;
            return false;
        }
        var callback = new Native.EnumCallback(Visit);
        try { _ = Native.EnumWindows(callback, 0); }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException) { return 0; }
        GC.KeepAlive(callback);
        return found;
    }

    private static string Text(nint window, Func<nint, StringBuilder, int, int> read)
    {
        var text = new StringBuilder(256);
        var length = read(window, text, text.Capacity);
        return length <= 0 ? "" : text.ToString(0, Math.Min(length, text.Length));
    }

    private static class Native
    {
        internal delegate bool EnumCallback(nint window, nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumCallback callback, nint parameter);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowThreadProcessId(nint window, out int process);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint GetWindow(nint window, int relationship);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetClassNameW(nint window, StringBuilder text, int capacity);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AllowSetForegroundWindow(int process);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint window);
    }
}
