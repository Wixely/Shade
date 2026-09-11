using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using CupriFace.Accessibility;

namespace CupriFace.Shell.Accessibility;

// The COM objects a UIA client (Narrator, NVDA, FlaUI…) actually talks to. They are thin and
// thread-agnostic on purpose: every read resolves this provider's PATH against the bridge's
// current immutable snapshot (published by the UI thread after each drawn frame), and every
// action is POSTED to the UI thread — no UIA thread ever touches the live document. A path that
// no longer resolves (the control disappeared in a rebuild) degrades to empty answers, which
// UIA clients handle as "element gone".
//
// [GeneratedComClass] is what makes these reachable from native code without the runtime's
// built-in COM support: the generator emits the vtables at compile time, so the same objects
// work JIT, trimmed and under NativeAOT. IComUnknown is implemented so a provider can be handed
// out as a bare IUnknown (GetPatternProvider, selection arrays) with a single COM identity.

/// <summary>The fragment root: represents the window's whole content (the "document" node).</summary>
[SupportedOSPlatform("windows")]
[GeneratedComClass]
internal sealed partial class UiaRootProvider :
    IComUnknown, IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot
{
    private readonly UiaBridge _bridge;
    public UiaRootProvider(UiaBridge bridge) => _bridge = bridge;

    public ProviderOptions GetProviderOptions() =>
        ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading;

    public IComUnknown? GetPatternProvider(int patternId) => null;

    public Variant GetPropertyValue(int propertyId) => propertyId switch
    {
        UiaIds.ControlTypeProperty => Variant.From(UiaIds.Pane),
        UiaIds.FrameworkIdProperty => Variant.From("CupriFace"),
        UiaIds.IsControlElementProperty => Variant.From(true),
        UiaIds.IsContentElementProperty => Variant.From(false),   // the window itself is chrome, not content
        _ => Variant.Empty,                                       // everything else comes from the HWND host
    };

    public IRawElementProviderSimple? GetHostRawElementProvider() => _bridge.HostProvider;

    public IRawElementProviderFragment? Navigate(NavigateDirection direction)
    {
        var root = _bridge.Current?.Root;
        if (root is null || root.Children.Count == 0) return null;
        return direction switch
        {
            NavigateDirection.FirstChild => _bridge.ProviderFor(root.Children[0]),
            NavigateDirection.LastChild => _bridge.ProviderFor(root.Children[^1]),
            _ => null,   // the root has no parent or siblings inside this fragment tree
        };
    }

    public nint GetRuntimeId() => 0;   // the host provider supplies the window's id

    public UiaRect GetBoundingRectangle() => _bridge.ToScreenRect(_bridge.Current?.Root.Bounds ?? default);

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus() { /* focusing the window is the OS's business; nothing to do */ }

    public IRawElementProviderFragmentRoot? GetFragmentRoot() => this;

    public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y)
    {
        if (_bridge.Current is not { } snap) return null;
        // Screen physical px → CSS px, then the deepest node whose on-screen box contains the
        // point — walking children in order and letting later (painted-on-top) matches win,
        // mirroring how hit-testing resolves overlaps.
        var cx = (float)((x - snap.OriginX) / snap.Scale);
        var cy = (float)((y - snap.OriginY) / snap.Scale);
        AccessibilityNode? best = null;
        void Walk(AccessibilityNode n)
        {
            var (bx, by, bw, bh) = n.Bounds;
            if (cx >= bx && cx < bx + bw && cy >= by && cy < by + bh) best = n;
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var c in snap.Root.Children) Walk(c);
        return best is null ? null : _bridge.ProviderFor(best);
    }

    public IRawElementProviderFragment? GetFocus() =>
        _bridge.Current is { FocusedPath: { } fp } snap && snap.ByPath.TryGetValue(fp, out var n)
            ? _bridge.ProviderFor(n)
            : null;
}

/// <summary>One provider per semantic node, identified by structural path (stable across the
/// engine's per-keystroke rebuilds). Implements every pattern; <see cref="GetPatternProvider"/>
/// gates which ones a given role advertises.</summary>
[SupportedOSPlatform("windows")]
[GeneratedComClass]
internal sealed partial class UiaNodeProvider :
    IComUnknown, IRawElementProviderSimple, IRawElementProviderFragment,
    IInvokeProvider, IToggleProvider, IRangeValueProvider, IValueProvider,
    ISelectionItemProvider, ISelectionProvider, IExpandCollapseProvider
{
    private readonly UiaBridge _bridge;
    private readonly string _path;
    private readonly int _runtimeId;

    public UiaNodeProvider(UiaBridge bridge, string path, int runtimeId)
    {
        _bridge = bridge;
        _path = path;
        _runtimeId = runtimeId;
    }

    internal string Path => _path;

    private AccessibilityNode? Node =>
        _bridge.Current is { } snap && snap.ByPath.TryGetValue(_path, out var n) ? n : null;

    // ---- IRawElementProviderSimple ----------------------------------------------------------

    public ProviderOptions GetProviderOptions() =>
        ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading;

    public IComUnknown? GetPatternProvider(int patternId) =>
        Node is { } n && UiaBridge.Supports(patternId, n) ? this : null;

    public Variant GetPropertyValue(int propertyId)
    {
        if (Node is not { } n) return Variant.Empty;
        return propertyId switch
        {
            UiaIds.NameProperty => Variant.From(n.Name),
            UiaIds.ControlTypeProperty => Variant.From(UiaBridge.ControlTypeOf(n.Role)),
            UiaIds.IsEnabledProperty => Variant.From(!n.Disabled),
            UiaIds.IsPasswordProperty => Variant.From(_bridge.Current?.PasswordPaths.Contains(_path) == true),
            UiaIds.IsKeyboardFocusableProperty => Variant.From(n.Focusable && !n.Disabled),
            UiaIds.HasKeyboardFocusProperty => Variant.From(n.Focused),
            UiaIds.AutomationIdProperty => Variant.From(n.AutomationId),
            UiaIds.ClassNameProperty => Variant.From(n.Role),
            UiaIds.FrameworkIdProperty => Variant.From("CupriFace"),
            UiaIds.IsControlElementProperty => Variant.From(true),
            UiaIds.IsContentElementProperty => Variant.From(true),
            // Scrolled past, or clipped away by an overflow ancestor. Narrator uses this to skip a
            // control rather than read the whole document aloud.
            UiaIds.IsOffscreenProperty => Variant.From(n.Offscreen),
            _ => Variant.Empty,
        };
    }

    public IRawElementProviderSimple? GetHostRawElementProvider() => null;

    // ---- IRawElementProviderFragment --------------------------------------------------------

    public IRawElementProviderFragment? Navigate(NavigateDirection direction)
    {
        if (Node is not { } n) return null;
        switch (direction)
        {
            case NavigateDirection.Parent:
                return n.Parent is { } p && p.Parent is not null
                    ? _bridge.ProviderFor(p)
                    : _bridge.Root;   // a top-level node's parent is the fragment root
            case NavigateDirection.FirstChild:
                return n.Children.Count > 0 ? _bridge.ProviderFor(n.Children[0]) : null;
            case NavigateDirection.LastChild:
                return n.Children.Count > 0 ? _bridge.ProviderFor(n.Children[^1]) : null;
            case NavigateDirection.NextSibling:
            case NavigateDirection.PreviousSibling:
            {
                var siblings = n.Parent?.Children ?? _bridge.Current?.Root.Children;
                if (siblings is null) return null;
                var i = siblings.IndexOf(n);
                if (i < 0) return null;
                i += direction == NavigateDirection.NextSibling ? 1 : -1;
                return i >= 0 && i < siblings.Count ? _bridge.ProviderFor(siblings[i]) : null;
            }
            default:
                return null;
        }
    }

    public nint GetRuntimeId() => SafeArrays.OfInt32([UiaIds.AppendRuntimeId, _runtimeId]);

    public UiaRect GetBoundingRectangle() =>
        Node is { } n ? _bridge.ToScreenRect(n.Bounds) : default;

    public nint GetEmbeddedFragmentRoots() => 0;

    public void SetFocus() => _bridge.Post(doc => doc.AccessibilityFocus(_path));

    public IRawElementProviderFragmentRoot? GetFragmentRoot() => _bridge.Root;

    // ---- Patterns ---------------------------------------------------------------------------

    public void Invoke() => _bridge.Post(doc => doc.AccessibilityActivate(_path));

    public void Toggle() => _bridge.Post(doc => doc.AccessibilityActivate(_path));

    public ToggleState GetToggleState() => Node?.Checked switch
    {
        true => ToggleState.On,
        false => ToggleState.Off,
        null => ToggleState.Indeterminate,
    };

    public void SetValue(double value)
    {
        if (Node?.Role is not "slider") throw new InvalidOperationException("Value is read-only.");
        _bridge.Post(doc => doc.AccessibilitySetValue(_path, value));
    }

    double IRangeValueProvider.GetValue() => Node?.Now ?? 0;
    bool IRangeValueProvider.GetIsReadOnly() => Node?.Role is not "slider";
    public double GetMaximum() => Node?.Max ?? 100;
    public double GetMinimum() => Node?.Min ?? 0;
    public double GetLargeChange() => (GetMaximum() - GetMinimum()) / 10;
    public double GetSmallChange() => 1;

    public void SetValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_bridge.Current?.EditableTextPaths.Contains(_path) != true)
            throw new InvalidOperationException("Text is not editable.");
        _bridge.Post(doc =>
        {
            // Recheck live state after queuing, so disabling a field cannot be
            // bypassed by an action accepted against an older snapshot.
            if (UiaBridge.CanSetText(doc, _path)) doc.AccessibilitySetText(_path, value);
        });
    }

    string? IValueProvider.GetValue()
    {
        // Check protection and read the value from one immutable snapshot. UIA
        // requires password Value queries to fail, even for visually masked text.
        var snapshot = _bridge.Current;
        if (snapshot?.PasswordPaths.Contains(_path) == true)
            throw new InvalidOperationException("Password values cannot be read through UI Automation.");
        return snapshot is not null && snapshot.ByPath.TryGetValue(_path, out var node) ? node.Value ?? string.Empty : null;
    }
    bool IValueProvider.GetIsReadOnly() => _bridge.Current?.EditableTextPaths.Contains(_path) != true;

    public void Select() => _bridge.Post(doc => doc.AccessibilityActivate(_path));
    public void AddToSelection() => _bridge.Post(doc => doc.AccessibilityActivate(_path));
    public void RemoveFromSelection() { /* every selectable here is single-select */ }
    public bool GetIsSelected() => Node is { } n && (n.Selected ?? n.Checked ?? false);
    public IRawElementProviderSimple? GetSelectionContainer() =>
        Node?.Parent is { } p && p.Parent is not null ? _bridge.ProviderFor(p) : null;

    public nint GetSelection()
    {
        if (Node is not { } n) return 0;
        var selected = new List<IComUnknown>();
        foreach (var c in n.Children)
            if (c.Selected ?? c.Checked ?? false) selected.Add(_bridge.ProviderFor(c));
        return selected.Count > 0 ? SafeArrays.OfUnknown(selected) : 0;
    }
    public bool GetCanSelectMultiple() => false;
    public bool GetIsSelectionRequired() => false;

    public void Expand()
    {
        if (Node is { Expanded: false }) _bridge.Post(doc => doc.AccessibilityActivate(_path));
    }
    public void Collapse()
    {
        if (Node is { Expanded: true }) _bridge.Post(doc => doc.AccessibilityActivate(_path));
    }
    public ExpandCollapseState GetExpandCollapseState() => Node?.Expanded switch
    {
        true => ExpandCollapseState.Expanded,
        false => ExpandCollapseState.Collapsed,
        null => ExpandCollapseState.LeafNode,
    };
}
