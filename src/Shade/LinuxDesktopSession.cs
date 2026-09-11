using System.Security.Cryptography;
using System.Text;

namespace Shade;

internal sealed record LinuxDesktopSession(bool UsesWayland, string MutexName)
{
    internal static LinuxDesktopSession Current() => Describe(Environment.UserName,
        Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("WAYLAND_SOCKET"));

    internal static LinuxDesktopSession Describe(string user, string? x11Display, string? waylandDisplay,
        string? runtimeDirectory, string? sessionType, string? inheritedSocket = null)
    {
        var wayland = sessionType == "wayland" || !string.IsNullOrEmpty(waylandDisplay) || !string.IsNullOrEmpty(inheritedSocket);
        string endpoint;
        if (wayland)
        {
            var display = string.IsNullOrEmpty(waylandDisplay) ? "wayland-0" : waylandDisplay;
            // Socket paths, not the optional Xwayland DISPLAY, identify a native Wayland desktop.
            // Full-path normalization makes a named socket and its absolute spelling equivalent.
            endpoint = Path.GetFullPath(Path.IsPathRooted(display) ? display : Path.Combine(runtimeDirectory ?? "", display));
        }
        else endpoint = x11Display ?? "";
        // Length-prefix fields rather than delimiters that could also occur in an endpoint.
        var key = user.Length + ":" + user + (wayland ? ":wayland:" : ":x11:") + endpoint;
        return new(wayland, "Shade.Desktop." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    }
}
