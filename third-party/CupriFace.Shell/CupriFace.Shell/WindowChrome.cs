using System.Runtime.InteropServices;
using SkiaSharp;

namespace CupriFace.Shell;

internal static partial class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int LightTextColorRef = 0x00f4f4f4;

    public static void TryEnableDarkMode(nint window, SKColor chromeColor)
    {
        if (!OperatingSystem.IsWindows() || window == 0)
        {
            return;
        }

        var enabled = 1;
        var result = DwmSetWindowAttribute(
            window,
            DwmwaUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));
        if (result != 0)
        {
            _ = DwmSetWindowAttribute(
                window,
                DwmwaUseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }

        var darkColor = chromeColor.Red | chromeColor.Green << 8 | chromeColor.Blue << 16;
        _ = DwmSetWindowAttribute(window, DwmwaBorderColor, ref darkColor, sizeof(int));
        _ = DwmSetWindowAttribute(window, DwmwaCaptionColor, ref darkColor, sizeof(int));
        var lightText = LightTextColorRef;
        _ = DwmSetWindowAttribute(window, DwmwaTextColor, ref lightText, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        nint window,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
