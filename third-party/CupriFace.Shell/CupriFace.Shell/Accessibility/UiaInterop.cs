using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace CupriFace.Shell.Accessibility;

// The one file in CupriFace that talks to Windows directly.
//
// UI Automation is a COM contract defined by the OS: a screen reader asks our window for a
// provider via WM_GETOBJECT, and everything after that is COM calls on the interfaces below.
// There is no fully managed route — Microsoft's managed wrapper (UIAutomationProvider.dll)
// lives in the WindowsDesktop framework, and referencing that would pull the whole desktop
// runtime into a self-contained publish (~4x the size) for the sake of two small assemblies.
//
// So this file mirrors the relevant slice of UIAutomationCore.idl by hand instead — through the
// SOURCE-GENERATED COM layer ([GeneratedComInterface] / [GeneratedComClass] / [LibraryImport]),
// not the runtime's built-in COM interop. The built-in kind is switched off for trimmed apps and
// absent outright under NativeAOT, which is how a published build came to open a window, draw a
// correct frame, and quietly serve no accessibility tree at all (#126). The generated kind is
// ordinary code the compiler can see, so it survives both.
//
// The rules that keep this correct:
//   - Member ORDER inside each interface is the COM vtable order. Never reorder, never insert.
//   - IDL properties are METHODS here (Get*), one per get-only property, in the same slot. The
//     generator does not accept C# properties on a generated interface (SYSLIB1091).
//   - GUIDs come verbatim from the IDL.
//   - Every bool says which native bool it is. UIA's IDL uses 4-byte BOOL; the generator refuses
//     an unannotated bool rather than guess, which is the right refusal.
//   - The generator does not marshal VARIANT or SAFEARRAY. Those two are built by hand below,
//     which is a few lines each and no longer than the attribute soup they replace.
//   - Anything the IDL types as IUnknown** is declared as IComUnknown, an empty interface carrying
//     the IUnknown IID, so the marshaller hands back our object's identity pointer.
// Everything is 64-bit only (SetWindowLongPtrW has no 32-bit export) — matching the RIDs we
// ship. Callers must be behind OperatingSystem.IsWindows().

internal enum NavigateDirection
{
    Parent = 0,
    NextSibling = 1,
    PreviousSibling = 2,
    FirstChild = 3,
    LastChild = 4,
}

[Flags]
internal enum ProviderOptions
{
    ServerSideProvider = 0x2,
    UseComThreading = 0x20,
}

internal enum ToggleState
{
    Off = 0,
    On = 1,
    Indeterminate = 2,
}

internal enum ExpandCollapseState
{
    Collapsed = 0,
    Expanded = 1,
    PartiallyExpanded = 2,
    LeafNode = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct UiaRect
{
    public double Left, Top, Width, Height;
}

/// <summary>
/// A COM VARIANT, laid out by hand: 24 bytes on x64 — a 2-byte type tag, six bytes of reserved
/// padding, then a 16-byte union of which this bridge uses the first 8. The receiver owns what we
/// put in it and frees it with VariantClear, so a BSTR here is allocated for it to release.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    public const ushort VT_EMPTY = 0, VT_I4 = 3, VT_R8 = 5, VT_BSTR = 8, VT_BOOL = 11;

    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public int I4;
    [FieldOffset(8)] public double R8;
    [FieldOffset(8)] public short VariantBool;   // VARIANT_TRUE is -1, not 1
    [FieldOffset(8)] public nint Bstr;

    public static Variant Empty => default;
    public static Variant From(int value) => new() { Vt = VT_I4, I4 = value };
    public static Variant From(double value) => new() { Vt = VT_R8, R8 = value };
    public static Variant From(bool value) => new() { Vt = VT_BOOL, VariantBool = value ? (short)-1 : (short)0 };
    public static Variant From(string? value) =>
        value is null ? default : new() { Vt = VT_BSTR, Bstr = Marshal.StringToBSTR(value) };
}

/// <summary>The IUnknown IID with no members: what an IDL <c>IUnknown**</c> becomes here. Returning
/// a provider through this type hands the caller its identity pointer, which it then QueryInterfaces
/// for the pattern it wants.</summary>
[GeneratedComInterface, Guid("00000000-0000-0000-C000-000000000046")]
internal partial interface IComUnknown
{
}

[GeneratedComInterface, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IRawElementProviderSimple
{
    ProviderOptions GetProviderOptions();

    IComUnknown? GetPatternProvider(int patternId);

    Variant GetPropertyValue(int propertyId);

    IRawElementProviderSimple? GetHostRawElementProvider();
}

[GeneratedComInterface, Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IRawElementProviderFragment
{
    IRawElementProviderFragment? Navigate(NavigateDirection direction);

    /// <summary>A SAFEARRAY of VT_I4, or null. Built by <see cref="SafeArrays.OfInt32"/>.</summary>
    nint GetRuntimeId();

    UiaRect GetBoundingRectangle();

    /// <summary>A SAFEARRAY of VT_UNKNOWN, or null.</summary>
    nint GetEmbeddedFragmentRoots();

    void SetFocus();

    IRawElementProviderFragmentRoot? GetFragmentRoot();
}

[GeneratedComInterface, Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    IRawElementProviderFragment? ElementProviderFromPoint(double x, double y);

    IRawElementProviderFragment? GetFocus();
}

[GeneratedComInterface, Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
internal partial interface IInvokeProvider
{
    void Invoke();
}

[GeneratedComInterface, Guid("56d00bd0-c4f4-433c-a836-1a52a57e0892")]
internal partial interface IToggleProvider
{
    void Toggle();
    ToggleState GetToggleState();
}

[GeneratedComInterface, Guid("36dc7aef-33e6-4691-afe1-2be7274b3d33")]
internal partial interface IRangeValueProvider
{
    void SetValue(double value);
    double GetValue();
    [return: MarshalAs(UnmanagedType.Bool)] bool GetIsReadOnly();
    double GetMaximum();
    double GetMinimum();
    double GetLargeChange();
    double GetSmallChange();
}

[GeneratedComInterface, Guid("c7935180-6fb3-4201-b174-7df73adbf64a")]
internal partial interface IValueProvider
{
    void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);
    [return: MarshalAs(UnmanagedType.BStr)] string? GetValue();
    [return: MarshalAs(UnmanagedType.Bool)] bool GetIsReadOnly();
}

[GeneratedComInterface, Guid("2acad808-b2d4-452d-a407-91ff1ad167b2")]
internal partial interface ISelectionItemProvider
{
    void Select();
    void AddToSelection();
    void RemoveFromSelection();
    [return: MarshalAs(UnmanagedType.Bool)] bool GetIsSelected();
    IRawElementProviderSimple? GetSelectionContainer();
}

[GeneratedComInterface, Guid("fb8b03af-3bdf-48d4-bd36-1a65793be168")]
internal partial interface ISelectionProvider
{
    /// <summary>A SAFEARRAY of VT_UNKNOWN, or null. Built by <see cref="SafeArrays.OfUnknown"/>.</summary>
    nint GetSelection();
    [return: MarshalAs(UnmanagedType.Bool)] bool GetCanSelectMultiple();
    [return: MarshalAs(UnmanagedType.Bool)] bool GetIsSelectionRequired();
}

[GeneratedComInterface, Guid("d847d3a5-cab0-4a98-8c32-ecb45c59ad24")]
internal partial interface IExpandCollapseProvider
{
    void Expand();
    void Collapse();
    ExpandCollapseState GetExpandCollapseState();
}

/// <summary>
/// The two SAFEARRAY shapes UIA asks a provider for. Ownership follows the COM convention: the
/// array we return belongs to the caller, which frees it with SafeArrayDestroy — and for an array
/// of IUnknown that also releases every element, so each pointer written in is a reference we
/// hand over rather than one we keep.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class SafeArrays
{
    private const ushort VT_I4 = 3, VT_UNKNOWN = 13;

    public static nint OfInt32(ReadOnlySpan<int> values)
    {
        var psa = SafeArrayCreateVector(VT_I4, 0, (uint)values.Length);
        if (psa == 0) throw new OutOfMemoryException("SafeArrayCreateVector");
        SafeArrayAccessData(psa, out var data).ThrowOnFailure();
        try { values.CopyTo(new Span<int>((void*)data, values.Length)); }
        finally { SafeArrayUnaccessData(psa); }
        return psa;
    }

    /// <summary>Each element is the object's IUnknown identity, obtained through the same marshaller
    /// the generated interfaces use — so a provider has one COM identity however it is reached.</summary>
    public static nint OfUnknown(IReadOnlyList<IComUnknown> objects)
    {
        var psa = SafeArrayCreateVector(VT_UNKNOWN, 0, (uint)objects.Count);
        if (psa == 0) throw new OutOfMemoryException("SafeArrayCreateVector");
        SafeArrayAccessData(psa, out var data).ThrowOnFailure();
        try
        {
            var slots = new Span<nint>((void*)data, objects.Count);
            for (var i = 0; i < objects.Count; i++)
                slots[i] = (nint)ComInterfaceMarshaller<IComUnknown>.ConvertToUnmanaged(objects[i]);
        }
        finally { SafeArrayUnaccessData(psa); }
        return psa;
    }

    private static void ThrowOnFailure(this int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [LibraryImport("oleaut32.dll")]
    private static partial nint SafeArrayCreateVector(ushort vt, int lowerBound, uint elements);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayAccessData(nint psa, out nint data);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayUnaccessData(nint psa);
}

[SupportedOSPlatform("windows")]
internal static partial class UiaNative
{
    public const uint WM_GETOBJECT = 0x003D;
    public const uint WM_DESTROY = 0x0002;
    public const int UiaRootObjectId = -25;
    public const int GWLP_WNDPROC = -4;

    internal delegate nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("uiautomationcore.dll")]
    public static partial nint UiaReturnRawElementProvider(
        nint hwnd, nuint wParam, nint lParam, IRawElementProviderSimple? el);

    [LibraryImport("uiautomationcore.dll")]
    public static partial int UiaHostProviderFromHwnd(nint hwnd, out IRawElementProviderSimple provider);

    [LibraryImport("uiautomationcore.dll")]
    public static partial int UiaRaiseAutomationEvent(IRawElementProviderSimple provider, int eventId);

    [LibraryImport("uiautomationcore.dll")]
    public static partial int UiaRaiseAutomationPropertyChangedEvent(
        IRawElementProviderSimple provider, int propertyId, Variant oldValue, Variant newValue);

    [LibraryImport("uiautomationcore.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UiaClientsAreListening();

    [LibraryImport("uiautomationcore.dll")]
    public static partial int UiaDisconnectProvider(IRawElementProviderSimple provider);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    public static partial nint CallWindowProcW(nint prevWndProc, nint hWnd, uint msg, nuint wParam, nint lParam);
}

// UIA integer ids (UIAutomationClient.h). Only the ones the bridge uses.
internal static class UiaIds
{
    // Properties
    public const int ControlTypeProperty = 30003;
    public const int NameProperty = 30005;
    public const int HasKeyboardFocusProperty = 30008;
    public const int IsKeyboardFocusableProperty = 30009;
    public const int IsEnabledProperty = 30010;
    public const int AutomationIdProperty = 30011;
    public const int ClassNameProperty = 30012;
    public const int HelpTextProperty = 30013;
    public const int IsControlElementProperty = 30016;
    public const int IsContentElementProperty = 30017;
    public const int IsPasswordProperty = 30019;
    public const int IsOffscreenProperty = 30022;
    public const int FrameworkIdProperty = 30024;
    public const int ValueValueProperty = 30045;
    public const int ValueIsReadOnlyProperty = 30046;
    public const int RangeValueValueProperty = 30047;
    public const int ToggleStateProperty = 30086;

    // Control types
    public const int Button = 50000;
    public const int Calendar = 50001;
    public const int CheckBox = 50002;
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int Hyperlink = 50005;
    public const int Image = 50006;
    public const int ListItem = 50007;
    public const int List = 50008;
    public const int MenuItem = 50011;
    public const int ProgressBar = 50012;
    public const int RadioButton = 50013;
    public const int Slider = 50015;
    public const int Tab = 50018;
    public const int Menu = 50009;
    public const int MenuBar = 50010;
    public const int Spinner = 50016;
    public const int TabItem = 50019;
    public const int Text = 50020;
    public const int ToolTip = 50022;
    public const int Tree = 50023;
    public const int TreeItem = 50024;
    public const int Custom = 50025;
    public const int Group = 50026;
    public const int Document = 50030;
    public const int Window = 50032;
    public const int Pane = 50033;
    public const int HeaderItem = 50035;
    public const int Table = 50036;
    public const int Separator = 50038;

    // Patterns
    public const int InvokePattern = 10000;
    public const int SelectionPattern = 10001;
    public const int ValuePattern = 10002;
    public const int RangeValuePattern = 10003;
    public const int ExpandCollapsePattern = 10005;
    public const int SelectionItemPattern = 10010;
    public const int TogglePattern = 10015;

    // Events
    public const int AutomationFocusChangedEvent = 20005;
    public const int InvokedEvent = 20009;

    // GetRuntimeId: first element of a non-root runtime id
    public const int AppendRuntimeId = 3;
}
