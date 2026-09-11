using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CupriFace.Accessibility;

namespace CupriFace.Shell.Accessibility;

/// <summary>
/// The macOS NSAccessibility bridge: exposes the engine's semantics tree
/// (<see cref="AccessibilityNode"/>) to VoiceOver. DESIGN.md §5's macOS leg, and the third sibling
/// of <see cref="UiaBridge"/> and <see cref="AtSpiBridge"/>.
///
/// HOW IT ATTACHES, and why that shape. macOS asks the window's content VIEW for accessibility, and
/// that view belongs to SDL — we cannot subclass it at compile time and must not modify it for
/// every window in the process. So the bridge builds a subclass of whatever class the view actually
/// is, at runtime, and re-points THAT ONE INSTANCE at it (<c>object_setClass</c>). This is exactly
/// the mechanism KVO uses: our accessibility methods answer, everything else falls through to SDL's
/// implementation untouched, and no other view in the process is affected.
///
/// Each node is represented by a small Objective-C object created the same way. It stores nothing
/// but the node's ID in its indexed ivars: ids are minted per STRUCTURAL PATH and never reused, so
/// an element VoiceOver is holding keeps meaning the same control across the per-keystroke rebuild
/// — the same guarantee the other two bridges give, for the same reason.
///
/// THREADING. AppKit delivers accessibility messages on the main thread, which is also the UI
/// thread, so they interleave between frames rather than during one. The bridge still reads only an
/// immutable snapshot published after each drawn frame, matching the other two bridges: it costs
/// nothing and it means no accessibility call can ever observe a half-rebuilt tree.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class NsAccessibilityBridge : IDisposable
{
    /// <summary>What every AX call reads: an immutable view of one painted frame.</summary>
    internal sealed record Snapshot(
        AccessibilityNode Root,
        IReadOnlyList<AccessibilityNode> ById,
        IReadOnlyDictionary<string, int> IdByPath,
        string? FocusedPath,
        double WindowX,          // content origin in AppKit screen space (bottom-left origin)
        double WindowY,
        double ContentHeight,
        double Scale);           // logical units -> points; NOT 1 on a scaled display

    // The IMPs are static (the Objective-C runtime calls plain function pointers), so they reach
    // the bridge through this. One window, one bridge — asserted by TryAttach refusing a second.
    private static NsAccessibilityBridge? _current;

    private readonly CupriFace.CupriDocument _doc;
    private readonly Action _requestFrame;
    private readonly string _appName;
    private readonly ConcurrentQueue<Action<CupriFace.CupriDocument>> _actions = new();

    private readonly nint _window;
    private readonly nint _view;
    private volatile Snapshot? _snapshot;

    // id → the Objective-C element standing for it, created once and reused.
    private readonly List<nint> _elements = new();
    private readonly Dictionary<string, int> _idByPath = new(StringComparer.Ordinal);
    private readonly List<string> _pathById = new();
    private readonly object _idLock = new();

    private static nint _elementClass;

    internal Snapshot? Current => _snapshot;

    private static readonly bool Trace =
        Environment.GetEnvironmentVariable("CUPRIFACE_NSA_DEBUG") is "1" or "true";

    private static void Log(string message)
    {
        if (Trace) Console.Error.WriteLine("[nsa] " + message);
    }

    /// <summary>Kill switch, mirroring CUPRIFACE_UIA and CUPRIFACE_ATSPI.</summary>
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("CUPRIFACE_NSA") is not ("0" or "false" or "FALSE");

    private NsAccessibilityBridge(nint window, nint view, CupriFace.CupriDocument doc,
        Action requestFrame, string appName)
    {
        _window = window;
        _view = view;
        _doc = doc;
        _requestFrame = requestFrame;
        _appName = appName;
    }

    /// <summary>Subclass the content view in place and start answering accessibility, or return
    /// null (with a note on stderr) if anything about that fails — a bridge that cannot attach must
    /// cost the app nothing but a line of output.</summary>
    public static NsAccessibilityBridge? TryAttach(nint nsWindow, CupriFace.CupriDocument doc,
        Action requestFrame, string appName)
    {
        try
        {
            if (_current is not null) return null;         // one window, one bridge
            if (nsWindow == 0) throw new InvalidOperationException("no NSWindow yet");

            var view = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
            if (view == 0) throw new InvalidOperationException("the window has no content view");

            var bridge = new NsAccessibilityBridge(nsWindow, view, doc, requestFrame, appName);
            _current = bridge;
            EnsureElementClass();
            bridge.SwizzleView();
            Log($"attached to NSWindow 0x{nsWindow:x}, content view 0x{view:x}");
            return bridge;
        }
        catch (Exception ex)
        {
            _current = null;
            Console.Error.WriteLine(
                $"[CupriFace] NSAccessibility bridge unavailable ({ex.GetType().Name}: {ex.Message}); continuing without it.");
            return null;
        }
    }

    // ---- building the Objective-C classes --------------------------------------------------------

    /// <summary>Re-point the content view at a runtime subclass that answers accessibility. Only
    /// this instance changes; SDL's class is left exactly as it was.</summary>
    private unsafe void SwizzleView()
    {
        var baseClass = ObjC.object_getClass(_view);
        var name = "CupriFaceAccessibleView";
        var cls = ObjC.objc_getClass(name);
        if (cls == 0)
        {
            cls = ObjC.objc_allocateClassPair(baseClass, name, 0);
            if (cls == 0) throw new InvalidOperationException("could not subclass the content view");

            ObjC.class_addMethod(cls, ObjC.Sel("isAccessibilityElement"),
                (nint)(delegate* unmanaged<nint, nint, byte>)&ViewIsElement, "c@:");
            ObjC.class_addMethod(cls, ObjC.Sel("accessibilityRole"),
                (nint)(delegate* unmanaged<nint, nint, nint>)&ViewRole, "@@:");
            ObjC.class_addMethod(cls, ObjC.Sel("accessibilityLabel"),
                (nint)(delegate* unmanaged<nint, nint, nint>)&ViewLabel, "@@:");
            ObjC.class_addMethod(cls, ObjC.Sel("accessibilityChildren"),
                (nint)(delegate* unmanaged<nint, nint, nint>)&ViewChildren, "@@:");
            // VoiceOver asks these two by name when it wants the whole tree at once.
            ObjC.class_addMethod(cls, ObjC.Sel("accessibilityChildrenInNavigationOrder"),
                (nint)(delegate* unmanaged<nint, nint, nint>)&ViewChildren, "@@:");
            ObjC.objc_registerClassPair(cls);
        }
        ObjC.object_setClass(_view, cls);
    }

    /// <summary>The class every node's element is an instance of. Built once; each instance carries
    /// its node id in the indexed ivars requested here.</summary>
    private static unsafe void EnsureElementClass()
    {
        if (_elementClass != 0) return;
        var cls = ObjC.objc_allocateClassPair(ObjC.objc_getClass("NSObject"),
            "CupriFaceAccessibleElement", sizeof(int));
        if (cls == 0) throw new InvalidOperationException("could not create the element class");

        void Add(string selector, nint imp, string types) =>
            ObjC.class_addMethod(cls, ObjC.Sel(selector), imp, types);

        Add("isAccessibilityElement", (nint)(delegate* unmanaged<nint, nint, byte>)&ElementIsElement, "c@:");
        Add("accessibilityRole", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementRole, "@@:");
        Add("accessibilitySubrole", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementSubrole, "@@:");
        Add("accessibilityLabel", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementLabel, "@@:");
        Add("accessibilityTitle", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementLabel, "@@:");
        Add("accessibilityValue", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementValue, "@@:");
        Add("accessibilityMinValue", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementMinValue, "@@:");
        Add("accessibilityMaxValue", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementMaxValue, "@@:");
        Add("accessibilityParent", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementParent, "@@:");
        Add("accessibilityChildren", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementChildren, "@@:");
        Add("accessibilityChildrenInNavigationOrder", (nint)(delegate* unmanaged<nint, nint, nint>)&ElementChildren, "@@:");
        Add("accessibilityFrame", (nint)(delegate* unmanaged<nint, nint, ObjC.NSRect>)&ElementFrame, "{CGRect={CGPoint=dd}{CGSize=dd}}@:");
        Add("accessibilityEnabled", (nint)(delegate* unmanaged<nint, nint, byte>)&ElementEnabled, "c@:");
        Add("isAccessibilityEnabled", (nint)(delegate* unmanaged<nint, nint, byte>)&ElementEnabled, "c@:");
        Add("isAccessibilityFocused", (nint)(delegate* unmanaged<nint, nint, byte>)&ElementFocused, "c@:");
        Add("accessibilityPerformPress", (nint)(delegate* unmanaged<nint, nint, byte>)&ElementPress, "c@:");
        Add("setAccessibilityValue:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&ElementSetValue, "v@:@");

        ObjC.objc_registerClassPair(cls);
        _elementClass = cls;
    }

    /// <summary>The element for a node id, created on first use and kept for the app's life. Ids are
    /// stable per structural path, so this cache is bounded by the number of distinct controls the
    /// app has ever shown, not by how many times it rebuilt them.</summary>
    private unsafe nint ElementFor(int id)
    {
        lock (_idLock)
        {
            while (_elements.Count <= id) _elements.Add(0);
            if (_elements[id] != 0) return _elements[id];

            var element = ObjC.Send(ObjC.Send(_elementClass, ObjC.Sel("alloc")), ObjC.Sel("init"));
            *(int*)ObjC.object_getIndexedIvars(element) = id;
            _elements[id] = element;
            return element;
        }
    }

    private static unsafe int IdOf(nint element) => *(int*)ObjC.object_getIndexedIvars(element);

    // ---- UI-thread side (same contract as the other two bridges) ----------------------------------

    /// <summary>Run queued AT actions on the UI thread. True if any ran (→ mark dirty).</summary>
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

    /// <summary>Publish a fresh snapshot after a drawn frame, and tell VoiceOver if focus moved.</summary>
    public void PublishFrame(float logicalWidth, float logicalHeight, float scale, (int X, int Y) clientOrigin)
    {
        var root = _doc.BuildAccessibilityTree(logicalWidth, logicalHeight);
        var byId = new List<AccessibilityNode>();
        var idByPath = new Dictionary<string, int>(StringComparer.Ordinal);
        string? focusedPath = null;

        void Index(AccessibilityNode n)
        {
            var id = IdFor(n.Path);
            while (byId.Count <= id) byId.Add(root);
            byId[id] = n;
            idByPath[n.Path] = id;
            if (n.Focused) focusedPath = n.Path;
            foreach (var c in n.Children) Index(c);
        }
        Index(root);

        // Geometry comes from the WINDOW, not from SDL's top-left screen position: AX coordinates
        // are AppKit's (origin bottom-left of the primary display), and mixing the two conventions
        // is how every control ends up mirrored about the middle of the screen.
        var frame = ObjC.SendRect(_window, ObjC.Sel("frame"));
        var content = ObjC.SendRect(_view, ObjC.Sel("frame"));

        if (_snapshot is null)
            Log($"first snapshot: {idByPath.Count} nodes, root role '{root.Role}'; " +
                $"window frame ({frame.X},{frame.Y} {frame.Width}x{frame.Height}), " +
                $"content {content.Width}x{content.Height}, logical {logicalWidth}x{logicalHeight}, scale {scale}");
        var previousFocus = _snapshot?.FocusedPath;
        _snapshot = new Snapshot(root, byId, idByPath, focusedPath,
            frame.X, frame.Y, content.Height, scale <= 0 ? 1 : scale);

        if (focusedPath is not null && focusedPath != previousFocus &&
            idByPath.TryGetValue(focusedPath, out var focusedId))
            PostFocusChanged(focusedId);
    }

    private int IdFor(string path)
    {
        lock (_idLock)
        {
            if (_idByPath.TryGetValue(path, out var id)) return id;
            id = _pathById.Count;
            _pathById.Add(path);
            _idByPath[path] = id;
            return id;
        }
    }

    private void PostFocusChanged(int id)
    {
        try
        {
            var name = ObjC.AppKitConstant("NSAccessibilityFocusedUIElementChangedNotification");
            if (name != 0) ObjC.NSAccessibilityPostNotification(ElementFor(id), name);
        }
        catch { /* a notification that cannot be posted must not break the frame that caused it */ }
    }

    private void Post(Action<CupriFace.CupriDocument> action)
    {
        _actions.Enqueue(action);
        _requestFrame();
    }

    // ---- the answers (called by AppKit through the runtime) ---------------------------------------

    private static (NsAccessibilityBridge Bridge, Snapshot Snap, AccessibilityNode Node)? Resolve(nint element)
    {
        if (_current is not { } bridge) return null;
        if (bridge._snapshot is not { } snap) return null;
        var id = IdOf(element);
        if (id < 0 || id >= snap.ById.Count) return null;
        return (bridge, snap, snap.ById[id]);
    }

    private static string NameOf(AccessibilityNode n) => n.Name ?? n.Value ?? "";

    [UnmanagedCallersOnly]
    private static byte ViewIsElement(nint self, nint sel) => 0;   // a container, not a stop

    [UnmanagedCallersOnly]
    private static nint ViewRole(nint self, nint sel) => ObjC.NSString(NsAccessibility.RoleGroup);

    [UnmanagedCallersOnly]
    private static nint ViewLabel(nint self, nint sel) =>
        ObjC.NSString(_current?._appName ?? "CupriFace");

    /// <summary>The view's children ARE the roots of our semantics tree — this is the join between
    /// what Cocoa owns and what the engine owns.</summary>
    [UnmanagedCallersOnly]
    private static nint ViewChildren(nint self, nint sel)
    {
        try
        {
            if (_current is not { } bridge || bridge._snapshot is not { } snap)
                return ObjC.NSArray([]);
            return ObjC.NSArray([bridge.ElementFor(snap.IdByPath[snap.Root.Path])]);
        }
        catch { return ObjC.NSArray([]); }
    }

    [UnmanagedCallersOnly]
    private static byte ElementIsElement(nint self, nint sel) =>
        Resolve(self) is { } r && NsAccessibility.IsElement(r.Node) ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly]
    private static nint ElementRole(nint self, nint sel) =>
        ObjC.NSString(Resolve(self) is { } r ? NsAccessibility.RoleOf(r.Node.Role) : NsAccessibility.RoleUnknown);

    [UnmanagedCallersOnly]
    private static nint ElementSubrole(nint self, nint sel) =>
        Resolve(self) is { } r && NsAccessibility.SubroleOf(r.Node) is { } sub ? ObjC.NSString(sub) : 0;

    [UnmanagedCallersOnly]
    private static nint ElementLabel(nint self, nint sel) =>
        Resolve(self) is { } r ? ObjC.NSString(NameOf(r.Node)) : 0;

    [UnmanagedCallersOnly]
    private static nint ElementValue(nint self, nint sel)
    {
        if (Resolve(self) is not { } r) return 0;
        return NsAccessibility.ValueOf(r.Node) switch
        {
            double d => ObjC.NSNumber(d),
            string s => ObjC.NSString(s),
            _ => 0,
        };
    }

    [UnmanagedCallersOnly]
    private static nint ElementMinValue(nint self, nint sel) =>
        Resolve(self) is { } r && NsAccessibility.HasValue(r.Node) ? ObjC.NSNumber(r.Node.Min ?? 0) : 0;

    [UnmanagedCallersOnly]
    private static nint ElementMaxValue(nint self, nint sel) =>
        Resolve(self) is { } r && NsAccessibility.HasValue(r.Node) ? ObjC.NSNumber(r.Node.Max ?? 100) : 0;

    [UnmanagedCallersOnly]
    private static nint ElementParent(nint self, nint sel)
    {
        if (Resolve(self) is not { } r) return 0;
        // The root's parent is the view; everything else's is its tree parent.
        if (r.Node.Parent is { } parent && r.Snap.IdByPath.TryGetValue(parent.Path, out var pid))
            return r.Bridge.ElementFor(pid);
        return r.Bridge._view;
    }

    /// <summary>An off-screen subtree is not handed over at all.
    ///
    /// This is where macOS genuinely differs from the other two platforms, and the difference is not
    /// a preference. UIA has IsOffscreen and AT-SPI has SHOWING: the node stays in the tree, carries
    /// a flag, and the screen reader skips it. VoiceOver has no such flag to honour — it navigates
    /// whatever <c>accessibilityChildren</c> returns — so the only way for content below the fold to
    /// be genuinely unreachable is to leave it out. (<c>isAccessibilityElement</c> answering NO stops
    /// VoiceOver LANDING on a node; it does not remove it from a client's walk, which is exactly
    /// what the gate caught.)</summary>
    private static bool Reachable(AccessibilityNode n)
    {
        if (!n.Offscreen) return true;
        // A clipped-away container can still hold something visible — absolute positioning escapes
        // its parent's box — so an off-screen node is only dropped when nothing under it shows.
        foreach (var child in n.Children)
            if (Reachable(child)) return true;
        return false;
    }

    [UnmanagedCallersOnly]
    private static nint ElementChildren(nint self, nint sel)
    {
        try
        {
            if (Resolve(self) is not { } r) return ObjC.NSArray([]);
            var kids = new List<nint>(r.Node.Children.Count);
            foreach (var child in r.Node.Children)
                if (Reachable(child) && r.Snap.IdByPath.TryGetValue(child.Path, out var cid))
                    kids.Add(r.Bridge.ElementFor(cid));
            return ObjC.NSArray(kids.ToArray());
        }
        catch { return ObjC.NSArray([]); }
    }

    /// <summary>Screen rectangle, in AppKit's bottom-left-origin space. Two conversions, and this
    /// method is most of the reason the file exists:
    ///
    /// SCALE. Node bounds are LOGICAL units, and AppKit works in points; on this CI display the
    /// factor is 0.897, so an unscaled 30-unit button claims to be 30 points when it is really
    /// 26.9 — an error that grows with distance from the origin until controls at the far edge sit
    /// outside the window entirely. <see cref="AtSpiBridge"/> scales for the same reason.
    ///
    /// ORIGIN. The engine measures down from the top-left of the content area; AppKit measures up
    /// from the bottom-left of the screen. Get this wrong and every rectangle is individually
    /// plausible and collectively mirrored.
    ///
    /// (Worth knowing when reading a gate log: the AX CLIENT api reports frames top-left-origin.
    /// macOS converts what is returned here, so the numbers a client prints are not these numbers.)
    /// </summary>
    [UnmanagedCallersOnly]
    private static ObjC.NSRect ElementFrame(nint self, nint sel)
    {
        if (Resolve(self) is not { } r) return default;
        var (x, y, w, h) = r.Node.Bounds;
        var scale = r.Snap.Scale;
        return new ObjC.NSRect(
            r.Snap.WindowX + x * scale,
            r.Snap.WindowY + (r.Snap.ContentHeight - (y + h) * scale),
            w * scale, h * scale);
    }

    [UnmanagedCallersOnly]
    private static byte ElementEnabled(nint self, nint sel) =>
        Resolve(self) is { } r && !r.Node.Disabled ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly]
    private static byte ElementFocused(nint self, nint sel) =>
        Resolve(self) is { } r && r.Node.Focused ? (byte)1 : (byte)0;

    /// <summary>AXPress — the same journey a real click takes, queued onto the UI thread.</summary>
    [UnmanagedCallersOnly]
    private static byte ElementPress(nint self, nint sel)
    {
        if (Resolve(self) is not { } r) return 0;
        if (NsAccessibility.ActionOf(r.Node) is null) return 0;
        var target = r.Node.Path;
        r.Bridge.Post(doc => doc.AccessibilityActivate(target));
        return 1;
    }

    /// <summary>A VoiceOver value write (dragging a slider by keyboard), routed through the same
    /// path a real drag takes.</summary>
    [UnmanagedCallersOnly]
    private static void ElementSetValue(nint self, nint sel, nint value)
    {
        if (Resolve(self) is not { } r) return;
        if (!NsAccessibility.HasValue(r.Node)) return;
        var number = ObjC.SendDouble(value, ObjC.Sel("doubleValue"));
        var target = r.Node.Path;
        r.Bridge.Post(doc => doc.AccessibilitySetValue(target, number));
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
