using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CupriFace.Accessibility;

namespace CupriFace.Shell.Accessibility;

/// <summary>
/// The Windows UI Automation bridge: exposes the engine's semantics tree
/// (<see cref="AccessibilityNode"/>) to screen readers and UIA clients, rooted on the window's
/// HWND. DESIGN.md §5's Windows leg, replacing the old do-nothing scaffold.
///
/// Threading model — the part that keeps this safe:
///   - UIA property/navigation calls arrive on arbitrary RPC threads. They only ever read the
///     current immutable SNAPSHOT (tree + path index + screen transform), swapped in whole by
///     the UI thread after each drawn frame. No UIA thread touches the live document.
///   - Actions (Invoke, Toggle, SetValue, SetFocus) are queued and drained by the UI thread's
///     per-frame tick; each runs through the document's ordinary interaction machinery.
///   - Focus changes are detected while publishing (path diff) and raised as UIA focus events.
///
/// This file's callers stay managed: the only native surface is UiaInterop.cs, which goes through
/// SOURCE-GENERATED COM rather than the runtime's built-in kind — so the bridge is ordinary code
/// the compiler can see, and comes up the same JIT, trimmed and under NativeAOT (#126). An attach
/// failure (the subclass refused, UIA absent) still disables the bridge rather than the app.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UiaBridge
{
    internal sealed record Snapshot(
        AccessibilityNode Root,
        Dictionary<string, AccessibilityNode> ByPath,
        IReadOnlySet<string> PasswordPaths,
        IReadOnlySet<string> EditableTextPaths,
        string? FocusedPath,
        float Scale,
        float OriginX,
        float OriginY);

    private readonly CupriFace.CupriDocument _doc;
    private readonly Action _requestFrame;
    private readonly nint _hwnd;
    private readonly nint _prevWndProc;
    private readonly UiaNative.WndProc _wndProcKeepAlive;   // roots the delegate the OS calls into

    private readonly ConcurrentQueue<Action<CupriFace.CupriDocument>> _actions = new();
    private readonly ConcurrentDictionary<string, UiaNodeProvider> _providers = new();
    private int _nextRuntimeId;
    private volatile Snapshot? _snapshot;
    private volatile bool _clientSeen;   // a WM_GETOBJECT arrived at least once

    internal Snapshot? Current => _snapshot;
    internal UiaRootProvider Root { get; }
    internal IRawElementProviderSimple? HostProvider { get; }

    /// <summary>Kill switch: CUPRIFACE_UIA=0 disables the bridge (mirrors CUPRIFACE_SOFTWARE).</summary>
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("CUPRIFACE_UIA") is not ("0" or "false" or "FALSE");

    /// <summary>Attach to the window, or return null (with a note on stderr) when UIA can't be
    /// served here — the app must keep running either way.</summary>
    public static UiaBridge? TryAttach(nint hwnd, CupriFace.CupriDocument doc, Action requestFrame)
    {
        try
        {
            return new UiaBridge(hwnd, doc, requestFrame);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CupriFace] UIA bridge unavailable ({ex.GetType().Name}: {ex.Message}); continuing without it.");
            return null;
        }
    }

    private UiaBridge(nint hwnd, CupriFace.CupriDocument doc, Action requestFrame)
    {
        _hwnd = hwnd;
        _doc = doc;
        _requestFrame = requestFrame;

        var hr = UiaNative.UiaHostProviderFromHwnd(hwnd, out var host);
        if (hr < 0) throw new InvalidOperationException($"UiaHostProviderFromHwnd failed (0x{hr:x8}).");
        HostProvider = host;
        Root = new UiaRootProvider(this);

        // Subclass the GLFW window so WM_GETOBJECT reaches us; everything else forwards to the
        // original proc. The delegate is kept in a field — if the GC collected it, the OS would
        // call a freed thunk.
        _wndProcKeepAlive = WndProcHook;
        _prevWndProc = UiaNative.SetWindowLongPtrW(
            hwnd, UiaNative.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive));
        if (_prevWndProc == 0)
            throw new InvalidOperationException("SetWindowLongPtr(GWLP_WNDPROC) returned null.");
    }

    private nint WndProcHook(nint hWnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == UiaNative.WM_GETOBJECT && (int)lParam == UiaNative.UiaRootObjectId)
        {
            // First contact: make sure a frame renders soon so the client sees a fresh tree.
            if (!_clientSeen) { _clientSeen = true; _requestFrame(); }
            return UiaNative.UiaReturnRawElementProvider(hWnd, wParam, lParam, Root);
        }
        if (msg == UiaNative.WM_DESTROY)
            UiaNative.UiaReturnRawElementProvider(hWnd, 0, 0, null);   // release UIA's references
        return UiaNative.CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
    }

    // ---- UI-thread side ---------------------------------------------------------------------

    /// <summary>Run queued AT actions on the UI thread. Returns true if any ran (→ mark dirty).</summary>
    public bool DrainActions()
    {
        var any = false;
        while (_actions.TryDequeue(out var action))
        {
            any = true;
            try { action(_doc); }
            catch { /* a stale path or a mid-rebuild race must never take the app down */ }
        }
        return any;
    }

    /// <summary>Publish a fresh snapshot after a drawn frame. Cheap no-op until a UIA client has
    /// actually asked for us (or is globally listening).</summary>
    public void PublishFrame(float logicalWidth, float logicalHeight, float scale, (int X, int Y) clientOrigin)
    {
        if (!_clientSeen && !UiaNative.UiaClientsAreListening()) return;

        var root = _doc.BuildAccessibilityTree(logicalWidth, logicalHeight);
        var byPath = new Dictionary<string, AccessibilityNode>();
        var passwordPaths = new HashSet<string>(StringComparer.Ordinal);
        var editableTextPaths = new HashSet<string>(StringComparer.Ordinal);
        string? focusedPath = null;
        void Index(AccessibilityNode n)
        {
            // Capture masking semantics on the UI thread, not from an arbitrary
            // UIA client thread or from assumptions about a field's label.
            if (n.Role == "textbox" && _doc.NodeAtPath(n.Path)?.Element?.HasAttribute("data-mask") == true)
                passwordPaths.Add(n.Path);
            if (n.Role == "textbox" && CanSetText(_doc, n.Path)) editableTextPaths.Add(n.Path);
            if (n.Path.Length > 0) byPath[n.Path] = n;
            if (n.Focused) focusedPath = n.Path;
            foreach (var c in n.Children) Index(c);
        }
        Index(root);

        var previous = _snapshot;
        var previousFocus = previous?.FocusedPath;
        _snapshot = new Snapshot(root, byPath, passwordPaths, editableTextPaths, focusedPath, scale <= 0 ? 1f : scale, clientOrigin.X, clientOrigin.Y);

        if (previous is not null && UiaNative.UiaClientsAreListening())
        {
            foreach (var (path, node) in byPath)
            {
                if (!previous.ByPath.TryGetValue(path, out var old) || old.Role != node.Role) continue;
                if (node.Disabled != old.Disabled)
                    RaiseProperty(node, UiaIds.IsEnabledProperty, !old.Disabled, !node.Disabled);
                if (node.Role is "button" or "slider" or "switch" or "checkbox" && node.Name != old.Name)
                    RaiseProperty(node, UiaIds.NameProperty, old.Name ?? "", node.Name ?? "");
                if (node.Role == "slider" && node.Now != old.Now)
                    RaiseProperty(node, UiaIds.RangeValueValueProperty, old.Now ?? 0, node.Now ?? 0);
                if (node.Role is "switch" or "checkbox" && node.Checked != old.Checked)
                    RaiseProperty(node, UiaIds.ToggleStateProperty, ToggleValue(old.Checked), ToggleValue(node.Checked));
                if (node.Role != "textbox") continue;
                var wasEditable = previous.EditableTextPaths.Contains(path);
                var isEditable = editableTextPaths.Contains(path);
                if (wasEditable != isEditable)
                    RaiseProperty(node, UiaIds.ValueIsReadOnlyProperty, !wasEditable, !isEditable);
                // Never include protected contents in event payloads, including
                // transitions into or out of password semantics.
                if (!passwordPaths.Contains(path) && !previous.PasswordPaths.Contains(path)
                    && (old.Value ?? "") != (node.Value ?? ""))
                    RaiseProperty(node, UiaIds.ValueValueProperty, old.Value ?? "", node.Value ?? "");
            }
        }

        // Tell the screen reader where focus went — this is what makes Tab talk.
        if (focusedPath is not null && focusedPath != previousFocus)
        {
            try { UiaNative.UiaRaiseAutomationEvent(ProviderFor(_snapshot.ByPath[focusedPath]), UiaIds.AutomationFocusChangedEvent); }
            catch { /* a client vanishing mid-event is its problem, not ours */ }
        }
    }

    // ---- Provider plumbing (any thread) ------------------------------------------------------

    private void RaiseProperty(AccessibilityNode node, int propertyId, object oldValue, object newValue)
    {
        Variant before = default, after = default;
        try
        {
            before = PropertyVariant(oldValue);
            after = PropertyVariant(newValue);
            UiaNative.UiaRaiseAutomationPropertyChangedEvent(ProviderFor(node), propertyId, before, after);
        }
        catch { /* disappearing clients must not interrupt the UI */ }
        finally
        {
            // These VARIANTs are borrowed by the API; retain ownership of BSTRs.
            if (before.Vt == Variant.VT_BSTR) Marshal.FreeBSTR(before.Bstr);
            if (after.Vt == Variant.VT_BSTR) Marshal.FreeBSTR(after.Bstr);
        }
    }

    private static int ToggleValue(bool? value) => value switch { true => 1, false => 0, null => 2 };
    private static Variant PropertyVariant(object value) => value switch
    {
        bool boolean => Variant.From(boolean), double number => Variant.From(number),
        int integer => Variant.From(integer), string text => Variant.From(text),
        _ => throw new ArgumentException("Unsupported UIA event value.")
    };

    // Called only while publishing or draining actions on the UI thread.
    internal static bool CanSetText(CupriFace.CupriDocument doc, string path)
    {
        var element = doc.NodeAtPath(path)?.Element;
        return element is not null && element.GetAttribute("role") == "textbox"
            && element.GetAttribute("data-bind-value") is { Length: > 0 }
            && !AccessibilityTree.IsDisabled(element)
            && !element.HasAttribute("readonly")
            && element.GetAttribute("aria-readonly") != "true";
    }

    internal UiaNodeProvider ProviderFor(AccessibilityNode node) =>
        _providers.GetOrAdd(node.Path, path =>
            new UiaNodeProvider(this, path, Interlocked.Increment(ref _nextRuntimeId)));

    internal void Post(Action<CupriFace.CupriDocument> action)
    {
        _actions.Enqueue(action);
        _requestFrame();   // wake the render loop so the tick drains promptly
    }

    internal UiaRect ToScreenRect((float X, float Y, float W, float H) cssBounds)
    {
        var snap = _snapshot;
        var scale = snap?.Scale ?? 1f;
        var ox = snap?.OriginX ?? 0f;
        var oy = snap?.OriginY ?? 0f;
        return new UiaRect
        {
            Left = ox + cssBounds.X * scale,
            Top = oy + cssBounds.Y * scale,
            Width = cssBounds.W * scale,
            Height = cssBounds.H * scale,
        };
    }

    // ---- Role maps --------------------------------------------------------------------------

    /// <summary>ARIA role → UIA control type (the scaffold's contract table, completed).</summary>
    internal static int ControlTypeOf(string role) => role switch
    {
        "button" => UiaIds.Button,
        "link" => UiaIds.Hyperlink,
        "checkbox" => UiaIds.CheckBox,
        "switch" => UiaIds.CheckBox,          // UIA has no switch type; Toggle carries the state
        "radio" => UiaIds.RadioButton,
        "radiogroup" => UiaIds.Group,
        "slider" => UiaIds.Slider,
        "progressbar" => UiaIds.ProgressBar,
        "spinbutton" => UiaIds.Spinner,
        "textbox" => UiaIds.Edit,
        "combobox" => UiaIds.ComboBox,
        "listbox" or "list" => UiaIds.List,
        "option" or "listitem" => UiaIds.ListItem,
        "tablist" => UiaIds.Tab,
        "tab" => UiaIds.TabItem,
        "tabpanel" or "navigation" => UiaIds.Pane,
        "tree" => UiaIds.Tree,
        "treeitem" => UiaIds.TreeItem,
        "group" => UiaIds.Group,
        "heading" or "status" or "alert" => UiaIds.Text,
        "image" or "img" => UiaIds.Image,
        "menu" => UiaIds.Menu,
        "menubar" => UiaIds.MenuBar,
        "menuitem" => UiaIds.MenuItem,
        "dialog" or "alertdialog" => UiaIds.Window,
        "tooltip" => UiaIds.ToolTip,
        "table" => UiaIds.Table,
        "row" => UiaIds.Group,
        "columnheader" => UiaIds.HeaderItem,
        "cell" => UiaIds.Custom,
        "separator" => UiaIds.Separator,
        "document" => UiaIds.Document,
        _ => UiaIds.Group,
    };

    /// <summary>Which pattern a role advertises (the other half of the contract table).</summary>
    internal static bool Supports(int patternId, AccessibilityNode n) => patternId switch
    {
        UiaIds.InvokePattern => n.Role is "button" or "link" or "menuitem",
        UiaIds.TogglePattern => n.Role is "checkbox" or "switch",
        UiaIds.SelectionItemPattern => n.Role is "radio" or "tab" or "option" or "treeitem",
        UiaIds.RangeValuePattern => n.Role is "slider" or "progressbar" or "spinbutton",
        UiaIds.ValuePattern => n.Role is "textbox" or "combobox",
        UiaIds.ExpandCollapsePattern => n.Expanded is not null,
        UiaIds.SelectionPattern => n.Role is "tablist" or "listbox" or "radiogroup",
        _ => false,
    };
}
