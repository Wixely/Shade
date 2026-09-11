using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CupriFace.Shell.Accessibility;

/// <summary>
/// The macOS interop quarantine: the SECOND and last file in this project that calls an OS API
/// directly (<see cref="UiaInterop"/> is the first). Everything Objective-C lives here so the rest
/// of the macOS bridge reads as ordinary C#.
///
/// Why any of this is needed: AT-SPI is D-Bus over a socket, so its bridge has no interop at all.
/// NSAccessibility is the opposite — the OS sends Objective-C messages to your view, so serving it
/// means building an Objective-C class AT RUNTIME whose method implementations are managed
/// function pointers. A CI probe established that this works end to end through libobjc with no
/// compiled shim (.github/workflows/nsa-probe.yml records the run).
///
/// The one trap worth stating loudly: <c>objc_msgSend</c> is variadic in the headers, but it must
/// be imported once PER SIGNATURE SHAPE. A call declared with the wrong shape does not fail — it
/// disagrees with the callee about which registers hold what, and returns plausible rubbish.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    /// <summary>CGRect/NSRect — CGFloat is <c>double</c> on arm64 and x86_64 alike.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NSRect
    {
        public double X, Y, Width, Height;
        public NSRect(double x, double y, double w, double h) { X = x; Y = y; Width = w; Height = h; }
    }

    // ---- the runtime ---------------------------------------------------------------------------
    [DllImport(Runtime)] internal static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Runtime)] internal static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Runtime)] internal static extern IntPtr objc_allocateClassPair(IntPtr super,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr extraBytes);
    [DllImport(Runtime)] internal static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Runtime)] internal static extern IntPtr object_getClass(IntPtr obj);
    [DllImport(Runtime)] internal static extern IntPtr object_setClass(IntPtr obj, IntPtr cls);
    /// <summary>The spare bytes requested via <c>extraBytes</c> — where each element stashes the
    /// node id it stands for, so no side table has to be kept in sync with object lifetimes.</summary>
    [DllImport(Runtime)] internal static extern IntPtr object_getIndexedIvars(IntPtr obj);

    [DllImport(Runtime)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    // ---- objc_msgSend, one import per shape ------------------------------------------------------
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, nuint b);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr self, IntPtr sel, double a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern void SendVoid(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern NSRect SendRect(IntPtr self, IntPtr sel);
    [DllImport(Runtime, EntryPoint = "objc_msgSend")] internal static extern double SendDouble(IntPtr self, IntPtr sel);

    // ---- AppKit --------------------------------------------------------------------------------
    /// <summary>How an app tells assistive technologies that something changed — the macOS
    /// counterpart of a UIA event or an AT-SPI signal.</summary>
    [DllImport(AppKit)] internal static extern void NSAccessibilityPostNotification(IntPtr element, IntPtr notification);

    // ---- conveniences ----------------------------------------------------------------------------
    private static readonly Dictionary<string, IntPtr> SelectorCache = new(StringComparer.Ordinal);

    /// <summary>A cached selector. Interning matters: these are looked up on every accessibility
    /// call, and a screen reader makes a lot of them.</summary>
    internal static IntPtr Sel(string name)
    {
        lock (SelectorCache)
        {
            if (SelectorCache.TryGetValue(name, out var sel)) return sel;
            sel = sel_registerName(name);
            SelectorCache[name] = sel;
            return sel;
        }
    }

    /// <summary>An autoreleased NSString. Every interesting accessibility answer is one of these.</summary>
    internal static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Send(objc_getClass("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    internal static string? ToManaged(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>An autoreleased NSArray over the given objects (empty array when there are none).</summary>
    internal static IntPtr NSArray(IntPtr[] items)
    {
        var cls = objc_getClass("NSArray");
        if (items.Length == 0) return Send(cls, Sel("array"));
        fixed (IntPtr* first = items)
            return Send(cls, Sel("arrayWithObjects:count:"), (IntPtr)first, (nuint)items.Length);
    }

    internal static IntPtr NSNumber(double value) =>
        Send(objc_getClass("NSNumber"), Sel("numberWithDouble:"), value);

    /// <summary>An AppKit global NSString constant (notification names). The exported symbol is a
    /// POINTER to the string object, so it needs one dereference.</summary>
    internal static IntPtr AppKitConstant(string symbol)
    {
        if (!NativeLibrary.TryLoad(AppKit, out var handle)) return IntPtr.Zero;
        return NativeLibrary.TryGetExport(handle, symbol, out var address)
            ? Marshal.ReadIntPtr(address)
            : IntPtr.Zero;
    }
}
