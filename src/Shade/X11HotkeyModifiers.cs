using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shade;

internal static class X11HotkeyModifiers
{
    [SupportedOSPlatform("linux")]
    internal static uint[] Read(nint display)
    {
        var pointer = X11Native.XGetModifierMapping(display);
        if (pointer == 0) throw new InvalidOperationException("Keyboard modifier map unavailable.");
        try
        {
            var map = Marshal.PtrToStructure<X11Native.ModifierKeymap>(pointer);
            if (map.KeysPerModifier is < 1 or > 256 || map.Keys == 0) throw new InvalidDataException("Invalid modifier map.");
            var keys = new byte[8 * map.KeysPerModifier]; Marshal.Copy(map.Keys, keys, 0, keys.Length);
            return Build(keys, map.KeysPerModifier,
                [X11Native.XKeysymToKeycode(display, 0xffe9), X11Native.XKeysymToKeycode(display, 0xffea)], // Alt L/R
                [X11Native.XKeysymToKeycode(display, 0xff7f), X11Native.XKeysymToKeycode(display, 0xff14),
                    X11Native.XKeysymToKeycode(display, 0xffe5)]); // Num, Scroll, Caps Lock
        }
        finally { X11Native.XFreeModifiermap(pointer); }
    }

    internal static uint[] Build(byte[] map, int width, byte[] altKeys, byte[] lockKeys)
    {
        if (width is < 1 or > 256 || map.Length != 8 * width) throw new ArgumentException("Invalid modifier map size.");
        bool Contains(int modifier, byte[] codes) => map.AsSpan(modifier * width, width).ContainsAny(codes.Where(c => c != 0).ToArray());
        uint locks = 2; // The core Lock modifier must also be tolerated (Caps/Shift Lock).
        for (var modifier = 0; modifier < 8; modifier++) if (Contains(modifier, lockKeys)) locks |= 1u << modifier;
        var bindings = new HashSet<uint>();
        for (var modifier = 3; modifier < 8; modifier++)
        {
            if (!Contains(modifier, altKeys)) continue;
            uint required = 1u | 4u | (1u << modifier); // Shift + Control + this Alt mapping.
            // A lock sharing a required bit cannot distinguish the requested gesture reliably.
            if ((required & locks) != 0) continue;
            for (uint subset = locks; ; subset = (subset - 1) & locks)
            {
                bindings.Add(required | subset);
                if (subset == 0) break;
            }
        }
        return bindings.Order().ToArray();
    }
}
