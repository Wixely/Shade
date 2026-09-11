using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shade;

[SupportedOSPlatform("linux")]
internal static class X11Native
{
    private const string X = "libX11.so.6";
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    internal struct Event
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(24)] public nint Display;
        [FieldOffset(32)] public nuint Window;
        [FieldOffset(40)] public nuint MessageType;
        [FieldOffset(40)] public int MappingRequest;
        [FieldOffset(48)] public int Format;
        [FieldOffset(56)] public nint Data;
        [FieldOffset(80)] public uint State;
        [FieldOffset(84)] public uint Keycode;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ModifierKeymap { public int KeysPerModifier; public nint Keys; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes
    {
        public nuint BackgroundPixmap, BackgroundPixel, BorderPixmap, BorderPixel;
        public int BitGravity, WindowGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nint EventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nuint Colormap, Cursor;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Hints
    {
        public nint Flags;
        public int Input, InitialState;
        public nuint IconPixmap, IconWindow;
        public int IconX, IconY;
        public nuint IconMask, WindowGroup;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ErrorHandler(nint display, nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int IoErrorHandler(nint display);
    [DllImport(X)] internal static extern int XInitThreads();
    [DllImport(X)] internal static extern nint XOpenDisplay(nint name);
    [DllImport(X)] internal static extern int XCloseDisplay(nint display);
    [DllImport(X)] internal static extern nint XSetErrorHandler(ErrorHandler handler);
    [DllImport(X)] internal static extern nint XSetIOErrorHandler(IoErrorHandler handler);
    [DllImport(X)] internal static extern nuint XDefaultRootWindow(nint display);
    [DllImport(X)] internal static extern int XDefaultScreen(nint display);
    [DllImport(X)] internal static extern nuint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport(X)] internal static extern nuint XGetSelectionOwner(nint display, nuint selection);
    [DllImport(X)] internal static extern nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint border, nuint borderPixel, nuint background);
    [DllImport(X)] internal static extern int XDestroyWindow(nint display, nuint window);
    [DllImport(X)] internal static extern int XChangeWindowAttributes(nint display, nuint window, nuint mask, ref Attributes attributes);
    [DllImport(X)] internal static extern int XSetWMHints(nint display, nuint window, ref Hints hints);
    [DllImport(X)] internal static extern int XStoreName(nint display, nuint window, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(X)] internal static extern int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode, nint[] data, int count);
    [DllImport(X)] internal static extern int XMapRaised(nint display, nuint window);
    [DllImport(X)] internal static extern int XRaiseWindow(nint display, nuint window);
    [DllImport(X)] internal static extern int XUnmapWindow(nint display, nuint window);
    [DllImport(X)] internal static extern int XMoveResizeWindow(nint display, nuint window, int x, int y, uint width, uint height);
    [DllImport(X)] internal static extern int XSelectInput(nint display, nuint window, nint mask);
    [DllImport(X)] internal static extern int XNextEvent(nint display, out Event next);
    [DllImport(X)] internal static extern int XSendEvent(nint display, nuint window, int propagate, nint mask, ref Event value);
    [DllImport(X)] internal static extern int XSync(nint display, int discard);
    [DllImport(X)] internal static extern int XFlush(nint display);
    [DllImport(X)] internal static extern byte XKeysymToKeycode(nint display, nuint keysym);
    [DllImport(X)] internal static extern nint XGetModifierMapping(nint display);
    [DllImport(X)] internal static extern int XFreeModifiermap(nint map);
    [DllImport(X)] internal static extern int XRefreshKeyboardMapping(ref Event mappingEvent);
    [DllImport(X)] internal static extern int XGrabKey(nint display, int key, uint modifiers, nuint window, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(X)] internal static extern int XUngrabKey(nint display, int key, uint modifiers, nuint window);
    [DllImport(X)] internal static extern int XGetGeometry(nint display, nuint window, out nuint root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
    [DllImport(X)] internal static extern int XGetInputFocus(nint display, out nuint focus, out int revert);
    [DllImport(X)] internal static extern int XFree(nint data);
    [DllImport("libXrandr.so.2")] internal static extern void XRRSelectInput(nint display, nuint root, int mask);
    [DllImport("libXrandr.so.2")] internal static extern int XRRQueryExtension(nint display, out int eventBase, out int errorBase);
    [DllImport("libXext.so.6")] internal static extern int XShapeQueryVersion(nint display, out int major, out int minor);
    [DllImport("libXext.so.6")] internal static extern void XShapeCombineRectangles(nint display, nuint window, int kind, int x, int y, nint rectangles, int count, int operation, int ordering);
    [DllImport("libXext.so.6")] internal static extern nint XShapeGetRectangles(nint display, nuint window, int kind, out int count, out int ordering);
}
