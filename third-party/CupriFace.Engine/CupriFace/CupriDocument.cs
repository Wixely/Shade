using System.Diagnostics;
using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CupriFace.Binding;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Layout;
using CupriFace.Paint;
using CupriFace.Resources;
using CupriFace.Style;
using CupriFace.Text;
using SkiaSharp;

namespace CupriFace;

/// <summary>
/// The public entry point: loads HTML + CSS, applies data binding, resolves styles,
/// and renders to a Skia canvas. Ties together Layers 1–5 of DESIGN.md.
/// </summary>
public sealed partial class CupriDocument : IDisposable
{
    private readonly string _templateHtml;
    private readonly string? _css;
    private object? _model;
    private ComponentRegistry? _components;

    private readonly FontService _fonts;
    private readonly LayoutEngine _layout;
    private readonly Painter _painter;
    private readonly SkiaRasterizer _rasterizer;
    private readonly Paint.ImageStore _images;

    private IDocument? _dom;
    private IDocument? _templateDom; // the template parsed once; each rebuild clones it (see Rebuild)
    private RenderNode _root = null!;
    private List<CssRule> _rules = new();   // reused by ReStyle (hover/active without a full rebuild)
    private Dictionary<string, List<Keyframe>> _keyframes = new();
    private List<CssRule>? _cachedRules;    // parsed once (CSS is immutable) and reused every rebuild
    private Dictionary<string, List<Keyframe>>? _cachedKeyframes;
    private float _viewportWidth = 1024f;
    private float _viewportHeight = 768f;
    // The last size laid out, and whether the CURRENT tree has been laid out at it. A rebuild/restyle
    // produces a fresh tree with no geometry; hosts lay out once per frame and then dispatch input, so
    // input arriving before that frame would hit-test a tree whose boxes are all zero — see EnsureLaidOut.
    private float _laidOutWidth, _laidOutHeight;
    private bool _layoutDirty = true;
    private bool _hasMedia;
    // Set once any resolved declaration used vh/vw/vmin/vmax. Such a document must re-resolve when
    // the viewport changes, exactly as an @media one does — without this its viewport lengths stay
    // pinned to whatever size they were first resolved at (the default, before any host laid out).
    private bool _hasViewportUnits;
    private readonly Style.TransitionEngine _transitions = new();

    // Hover + drag + text-focus state
    private readonly List<IElement> _hoverChain = new();
    private readonly List<IElement> _activeChain = new(); // :active — the pressed element + ancestors
    private string? _focusKey;  // the focused field's bound path (survives rebuilds)
    private string _focusInputMode = "", _focusEnterHint = "", _focusPlaceholder = "";
    private bool _focusNumeric; // focused field is validated as a number
    private bool _focusMultiline; // focused field is a textarea (Enter inserts a newline)
    private bool _focusMask;    // focused field masks its text (data-mask, e.g. <cupri-password>)
    private bool _focusSubmitOnEnter; // focused field submits on Enter (Shift+Enter still inserts)
    private string? _focusTagList;    // focused field appends to this comma-separated path on Enter
    private int _maskRevealPos = -1;          // index of a masked char being briefly "peeked" after typing; -1 = none
    // IME composition (preedit): a marked range INSIDE _editBuffer — rendered underlined, never
    // committed to the model until the IME says so. -1 start = no composition.
    private int _compStart = -1;
    private int _compLen;
    private (string Value, int Caret, int Anchor)? _compUndo;   // pre-composition snapshot: ONE undo step per composition
    private double _maskRevealStart = double.NaN; // Animate() clock time the peek began (NaN = not yet stamped)
    private double? _focusMin, _focusMax; // numeric field bounds (for validation/clamping)
    private bool _focusRequired;          // focused field's validation rules (for the mid-edit red border)
    private string? _focusPattern;
    private int? _focusMinLen;
    private readonly HashSet<string> _touched = new(); // fields visited (blurred) → their error text may show
    private bool _validateAll;            // set by ValidateAll() (form submit) → show every field's error
    private string? _editBuffer; // raw text being edited (permissive); validated/committed on blur
    private int _caret;
    private int _selAnchor;      // selection anchor; selection is [min(anchor,caret), max]. anchor==caret ⇒ none.
    private int _listHi = -1;    // highlighted option index for a focused listbox field (combobox); -1 = none
    private int _gridHi = -1;    // highlighted cell index for an open [data-gridnav] overlay (datepicker); -1 = none
    private bool _textDrag;      // a mouse drag is extending the text selection
    private int _kbIndex = -1;      // keyboard focus: index into the focusable list (-1 = none)
    private bool _focusVisible;     // show the focus ring? true after Tab, false after a mouse click
    private bool _dragging;
    // A live window drag (data-window-drag) and the point it was grabbed at, in window pixels.
    private bool _windowDrag;
    private float _windowDragX, _windowDragY;
    private float _dragX0, _dragInnerW, _dragPad;
    private double _dragMin, _dragMax;
    private string? _dragPath;
    private bool _caretMoved;           // caret changed since last render → scroll it into view once
    private RenderNode? _scrollDrag;    // scrollable node whose scrollbar thumb is being dragged
    private float _scrollDragY0, _scrollDragScroll0;
    // Each <cupri-virtual> list's scroll offset by its data-repeat path, so the next rebuild windows it to
    // the rows in view. Updated when a virtual list scrolls (which then rebuilds to re-window).
    private readonly Dictionary<string, double> _virtualScroll = new();
    // Measured row pitches (margin-box heights) per <cupri-virtual>, keyed like _virtualScroll and
    // index-aligned with the bound collection; ≤0 = never measured, so windowing falls back to the
    // item-height estimate. Filled by CaptureVirtualHeights after every layout; cleared when the
    // list's content width changes — rows are wrap-sized, so a resize invalidates every pitch (#67).
    private readonly Dictionary<string, List<float>> _virtualHeights = new();
    private readonly Dictionary<string, float> _virtualWidth = new();
    // Whether each virtual list sat at its bottom last frame — anchor="bottom" pins only while the
    // user is actually there, so one scroll up releases the pin until they return.
    private readonly Dictionary<string, bool> _virtualAtBottom = new();
    // Scroll offsets decided while BINDING (bottom anchoring, prepend compensation): the node they
    // belong to is only built afterwards — applied and synced into _virtualScroll after RestoreScroll.
    // Keys the binder pinned to the bottom also land in _virtualPinned, so the capture pass can snap
    // them to the REAL bottom once the layout knows it exactly.
    private readonly Dictionary<string, double> _pendingVirtualScroll = new();
    private readonly HashSet<string> _virtualPinned = new();
    // Scroll/resize state keyed by structural path, kept BEYOND the lifetime of any one tree: a
    // `display:none` page builds no children, so what it had cannot be captured when it is hidden.
    private readonly Dictionary<string, NodeState> _scrollMemory = new();
    private RenderNode? _resizeDrag;    // node whose resize grip is being dragged
    private float _resizeX0, _resizeY0, _resizeW0, _resizeH0;

    // Drag-to-reorder: the list being reordered, its item nodes, the source/target slot, the grab Y, each
    // item's original mid-line (for slot hit-testing), and the per-slot shift distance.
    // Drag-to-reorder — one list, or several columns of a kanban board (all .cupri-reorder lists under a
    // shared .cupri-board). The lifted card follows the pointer across columns; the source column closes
    // its gap and the target column opens one; on drop OnReorder carries the source + target lists.
    private RenderNode? _reorderList;      // the source column the drag started in
    private RenderNode? _reorderCard;      // the lifted card (Dragging = true)
    private List<RenderNode>? _reorderItems; // every card across the group's columns (for the ease pass)
    private readonly record struct ReorderCol(RenderNode List, List<RenderNode> Items, float[] Mids, float Left, float Right);
    private List<ReorderCol>? _reorderCols; // per-column geometry for target/gap computation
    private int _reorderFromCol, _reorderToCol, _reorderFrom, _reorderTo;
    private float _reorderX0, _reorderY0, _reorderShift;
    private double _reorderAnimT = double.NaN; // previous ease time, for frame-rate-independent smoothing
    private Action<ReorderEvent>? _onReorder;

    // Split pane: the two panels either side of the divider being dragged, the drag axis, the grab point,
    // and the pair's initial pixel sizes + total flex-grow (kept constant so other panels don't move).
    private RenderNode? _splitA, _splitB;
    private bool _splitVertical;
    private float _splitStart, _splitPA0, _splitPB0, _splitGSum;

    // The last pointer hit-test (from UpdateHover), reused by CursorAt so a host's move→cursor pair costs
    // one tree walk, not two. Valid only for the same coordinates on the SAME tree — _lastHitRoot guards
    // against a restyle/rebuild having replaced the nodes in between.
    private RenderNode? _lastHit, _lastHitRoot;
    private float _lastHitX = float.NaN, _lastHitY = float.NaN;

    // Table column resize: the bound width-list path, the dragged column, the grab origin (its content
    // width + pointer x), and the list as it stood when the drag began. All value types (the width list is
    // written to the model each move, which rebuilds), so nothing here dangles across the per-move rebuild.
    private string? _colPath;
    private int _colIndex;
    private float _colStartW, _colStartX, _colMaxW;
    private string[] _colList = [];

    /// <summary>Narrowest a dragged column may get — enough to stay grabbable and show a little content.</summary>
    private const float MinColumnW = 24f;

    /// <summary>A drag-to-reorder drop: the source list, the item's index in it (<see cref="From"/>), and the
    /// target index (<see cref="To"/>) in <see cref="ToList"/>. For a single list (or a within-column move)
    /// <see cref="ToList"/> equals <see cref="List"/>; across kanban columns they differ. Register a handler
    /// with <see cref="OnReorder"/>; it moves the item from the source list to the target.</summary>
    public readonly record struct ReorderEvent(IElement List, int From, int To, IElement ToList);
    public CupriDocument OnReorder(Action<ReorderEvent> handler) { _onReorder = handler; return this; }

    // Right-click context menu (Cut/Copy/Paste/Select-all) over a text field. The engine owns
    // opening/positioning/rendering/dismissing it; the host performs the chosen clipboard action.
    private bool _ctxOpen;
    private float _ctxX, _ctxY;
    private bool _ctxHasSelection;  // enables Cut/Copy
    private bool _ctxHasText;       // enables Select All
    // A custom <cupri-context-menu> region's menu, opened at (_ctxX,_ctxY). Identified by the host's
    // document-order index (stable across rebuilds; element identity and generated ids are not). -1 = none.
    private int _ctxCustomIndex = -1;

    /// <summary>Raised when a context-menu item is chosen. The host performs the clipboard action
    /// (via <see cref="CopySelection"/>/<see cref="CutSelection"/>/<see cref="DispatchKey"/> +
    /// its own clipboard), keeping platform clipboard code out of the engine.</summary>
    public event Action<Interaction.ContextCommand>? ContextRequested;

    /// <summary>Raised when a link (<c>&lt;a href&gt;</c>) is activated with a non-anchor href (see
    /// <see cref="Interaction.NavigateEvent"/>). In-page <c>#anchor</c> links are scrolled into view by the
    /// engine and do not raise this. Multicast: an app can route internal hrefs (e.g. switch a view) while a
    /// host opens external ones in a browser — the engine itself opens nothing (that's a host concern).</summary>
    public event Action<Interaction.NavigateEvent>? Navigated;

    /// <summary>Raised when an element carrying <c>data-window-command</c> (e.g. a video's
    /// fullscreen button) is activated. The engine owns pixels, not the OS window — the host
    /// performs the command (desktop: window state; web: the Fullscreen API). Escape-to-exit is
    /// also host-side: when <see cref="DispatchKey"/> returns unhandled for Escape and the window
    /// is fullscreen, the host exits it (so overlays keep winning Escape first).</summary>
    public event Action<Interaction.WindowCommand>? WindowCommandRequested;

    /// <summary>Raised repeatedly while a drag is live on an element marked <c>data-window-drag</c>:
    /// the title bar a frameless window does not have. Carries how far the pointer has moved from
    /// where it was pressed, for the host to add to the window's position.
    ///
    /// <para>Same engine→host split as <see cref="WindowCommandRequested"/> — the engine owns pixels,
    /// not the OS window. A host with nothing to move (a browser page, an Android activity) simply
    /// does not subscribe, and the drag then does nothing rather than misbehaving.</para></summary>
    public event Action<Interaction.WindowMove>? WindowMoveRequested;

    /// <summary>The colour the page itself paints behind everything — <c>body</c>'s computed
    /// background, or the root's if the body is transparent. A host needs it for the surfaces the
    /// document does NOT paint: the strip a soft keyboard animates over, the bars a fullscreen
    /// window letterboxes with. Leaving those the platform default is why a dark app flashed white
    /// every time the keyboard opened. Transparent when the page genuinely has no background.</summary>
    public SkiaSharp.SKColor PageBackground
    {
        get
        {
            for (var n = _root; n is not null; n = n.Children.Count > 0 ? n.Children[0] : null)
            {
                if (n.Style.Background.Alpha > 0) return n.Style.Background;
                if (n.Tag is "body") break;              // don't descend into the app's own content
            }
            return SkiaSharp.SKColors.Transparent;
        }
    }

    /// <summary>Raise a clipboard/edit command as though the engine's own context menu had asked
    /// for it — the seam an Android IME's edit menu routes into, so a paste from the keyboard, from
    /// our menu and from Ctrl+V all take the same path through the host.</summary>
    public void RequestContextCommand(Interaction.ContextCommand command) =>
        ContextRequested?.Invoke(command);

    /// <summary>Raised when the app says a form was submitted. A password manager only offers to
    /// SAVE a new credential when the app tells it the entry is finished — filling is passive, but
    /// saving needs a moment declared. On Android the host answers this with
    /// <c>AutofillManager.Commit()</c>; elsewhere nothing listens and nothing breaks.</summary>
    public event Action? FormSubmitted;

    /// <summary>Declare that the user finished entering a form — the sign-in button, the last field
    /// of a wizard. Call it after the model has the final values.</summary>
    public void SubmitForm() => FormSubmitted?.Invoke();

    /// <summary>Ask the host for a window command from code — the same seam
    /// <c>data-window-command</c> uses, for apps whose own logic decides (a settings switch, a
    /// presentation mode, a kiosk that starts fullscreen). No-op when no host is listening, exactly
    /// like the attribute path: an embedded or headless consumer simply has no window to command.</summary>
    public void RequestWindowCommand(Interaction.WindowCommand command) =>
        WindowCommandRequested?.Invoke(command);

    // Engine-owned toast stack (doc.Toast). Each toast slides in, waits, then slides out and is removed —
    // driven by Animate. Entering/Leaving render off-screen; the flip to/from Shown is what the transition
    // engine animates (paint-only). Rendered bottom-right by InjectToaster + the ToasterComponent's CSS.
    private enum ToastPhase { Entering, Shown, Leaving }
    private sealed class ToastEntry { public string Msg = ""; public string Kind = ""; public ToastPhase Phase; public double T = double.NaN; }
    private readonly List<ToastEntry> _toasts = new();
    private const double ToastShowSeconds = 3.6, ToastExitSeconds = 0.36;
    private bool ToastsPending => _toasts.Count > 0;

    /// <summary>Raise a transient toast (bottom-right stack). <paramref name="kind"/> may be
    /// <c>"success"</c> or <c>"error"</c> to tint it. It slides in, sits a few seconds, then slides out.</summary>
    public void Toast(string message, string kind = "")
    {
        _toasts.Add(new ToastEntry { Msg = message, Kind = kind });
        Rebuild(); // render it (off-screen); Animate flips it in on the next frame
    }

    // Per-field undo/redo history (cleared on focus change). A snapshot of the edit buffer + caret.
    private readonly record struct EditState(string Buffer, int Caret, int Anchor);
    private readonly List<EditState> _undo = new();
    private readonly List<EditState> _redo = new();
    private bool _typingGroup;          // coalesce a run of printable chars into one undo step

    private CupriDocument(string html, string? css)
    {
        _templateHtml = html;
        _css = css;
        _fonts = new FontService();
        _images = new Paint.ImageStore();
        _layout = new LayoutEngine(_fonts, _images, Surfaces);
        _painter = new Painter(_images, Surfaces);
        _rasterizer = new SkiaRasterizer(_fonts);
    }

    /// <summary>Live pixel producers (video players, future 3D viewports), keyed by the element
    /// attribute <c>data-cupri-surface</c>. Backends register their sources here; a playing
    /// surface keeps the render loop live via <see cref="HasActiveAnimations"/>.</summary>
    public Paint.SurfaceRegistry Surfaces { get; } = new();

    /// <summary>Register the assembly used to resolve embedded media sources (a bare
    /// <c>src="Assets/logo.png"</c> on a <c>&lt;cupri-image&gt;</c> — and equally a
    /// <c>&lt;cupri-video&gt;</c>'s clip). Data URIs, URLs and file paths need no assembly.</summary>
    public CupriDocument UseImages(System.Reflection.Assembly assembly)
    {
        _images.SetAssembly(assembly);
        _sourceAssembly = assembly;
        return this;
    }

    private System.Reflection.Assembly? _sourceAssembly; // shared by image + video resolution

    /// <summary>Policy for remote (<c>http(s)</c>) image URLs (https-only, size cap, host allow-list…).
    /// Defaults are strict; override to e.g. allow a specific host.</summary>
    public CupriDocument UseImageUrlOptions(Resources.CupriSourceOptions options)
    {
        _images.UrlOptions = options;
        return this;
    }

    // ---- Video wiring (<cupri-video> ↔ the host-registered backend) --------------------------
    // The engine owns the seam, not the codecs: a backend opens players; the document owns their
    // lifetime by structural presence — a player exists while its src appears in the DOM, and is
    // disposed when it no longer does (a hidden section stops its video).
    private Media.IVideoBackend? _videoBackend;
    private readonly Dictionary<string, Media.IVideoPlayer> _videoPlayers = new(StringComparer.Ordinal);
    private int _videoStateChanged; // set (any thread) by Ended → consumed on the UI thread
    private string? _fullscreenVideo; // src of the video currently element-fullscreened (web model: one at most)

    /// <summary>Register the video backend (host composition root — see <see cref="Media.IVideoBackend"/>).</summary>
    private List<Action<AngleSharp.Dom.IDocument>>? _rebuiltHandlers;

    /// <summary>Run <paramref name="handler"/> after every rebuild, once components have expanded and
    /// bindings are concrete — the same moment the engine wires its own video players.
    ///
    /// <para>This is what a surface producer living outside the engine needs. Registering an
    /// <see cref="Paint.ISurfaceSource"/> is only half of it; something has to notice that an element
    /// asking for one has appeared, and that another's element has gone, and the DOM is rebuilt on
    /// every keystroke and every model change. Without this an optional package can ship a component
    /// that expands correctly and renders nothing forever.</para>
    ///
    /// <para>Handlers see the DOM the frame is about to be built from, so mutating it is allowed and
    /// is how a producer publishes what it learned (a natural size, a poster). Keep it cheap: this runs
    /// on the rebuild path, which is also the typing path.</para></summary>
    public CupriDocument OnRebuilt(Action<AngleSharp.Dom.IDocument> handler)
    {
        (_rebuiltHandlers ??= []).Add(handler);
        Refresh();                     // wire whatever is already in the tree
        return this;
    }

    public CupriDocument UseVideo(Media.IVideoBackend backend)
    {
        _videoBackend = backend;
        Refresh(); // wire any <cupri-video> already in the tree
        return this;
    }

    // Runs each rebuild, right after component expansion: open/adopt a player per visible source,
    // reflect its transport state into the fresh DOM's controls, retire players whose element is
    // gone. (Expansion skips display:none subtrees, so switching a section away stops its video.)
    private void SyncVideos(AngleSharp.Dom.IDocument dom)
    {
        HashSet<string>? seen = null;
        foreach (var el in dom.QuerySelectorAll("[data-cupri-video]"))
        {
            if (el.GetAttribute("data-cupri-video") is not { Length: > 0 } src) continue;
            (seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(src);
            if (GetOrOpenPlayer(src, el) is { } player)
                Components.Controls.VideoComponent.SyncControls(el, player);
            else
                Components.Controls.VideoComponent.MarkInert(el);   // no backend: honest controls
            // The fresh DOM starts windowed; re-mark the fullscreen one each rebuild.
            if (src == _fullscreenVideo)
                Components.Controls.VideoComponent.ApplyFullscreenState(el);
        }

        // The fullscreen video's element left the DOM (section switched away) — nothing is
        // fullscreen any more; the WINDOW state is the host's, released via the same event.
        if (_fullscreenVideo is not null && (seen is null || !seen.Contains(_fullscreenVideo)))
        {
            _fullscreenVideo = null;
            WindowCommandRequested?.Invoke(Interaction.WindowCommand.ExitFullscreen);
        }

        if (_videoPlayers.Count == 0) return;
        List<string>? gone = null;
        foreach (var src in _videoPlayers.Keys)
            if (seen is null || !seen.Contains(src)) (gone ??= new List<string>()).Add(src);
        if (gone is null) return;
        foreach (var src in gone)
        {
            try { _videoPlayers[src].Dispose(); } catch { /* a dying decoder must not kill the rebuild */ }
            _videoPlayers.Remove(src);
            Surfaces.Unregister("video:" + src);
        }
    }

    private Media.IVideoPlayer? GetOrOpenPlayer(string src, AngleSharp.Dom.IElement el)
    {
        if (_videoPlayers.TryGetValue(src, out var existing)) return existing;
        if (_videoBackend is null) return null; // no backend on this host — poster only

        Media.IVideoPlayer player;
        // The source carries the SAME resolution pipeline images use (embedded / file / data: /
        // policied https) — the developer picks the scheme, every backend honours it.
        var source = new Media.VideoSource(src, _sourceAssembly, _images.UrlOptions);
        try { player = _videoBackend.Open(source); }
        catch { return null; } // an unusable source must not take the app down; the poster stays

        _videoPlayers[src] = player;
        Surfaces.Register("video:" + src, player.Surface);
        player.Ended += () => System.Threading.Interlocked.Exchange(ref _videoStateChanged, 1);
        player.Loop = el.HasAttribute("data-video-loop");
        player.Muted = el.HasAttribute("data-video-muted");
        // The autoplay policy every host shares (the web cannot do otherwise, so nobody does):
        // autoplay starts only when muted; unmuted autoplay stays on the poster's play button.
        if (el.HasAttribute("data-video-autoplay") && player.Muted) player.Play();
        return player;
    }

    // Transport commands from the controls / the frame itself. DELIBERATELY synchronous inside
    // input dispatch: the web backend needs the user gesture still on the stack when the call
    // reaches video.play() (browsers refuse unmuted playback otherwise).
    private bool VideoCommand(RenderNode node, string cmd)
    {
        string? src = null;
        for (var n = node; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("data-cupri-video") is { Length: > 0 } s) { src = s; break; }
        if (src is null) return false;

        // Fullscreen is the video's OWN fullscreen (web semantics): the element covers the
        // viewport in the top layer AND the window goes OS-fullscreen — together they make the
        // video fill the screen. Needs no decoder (a poster fullscreens fine), so it's handled
        // before the player lookup; still synchronous inside the input dispatch, because the web
        // host's requestFullscreen demands the user gesture on the stack.
        if (cmd == "fullscreen")
        {
            var entering = _fullscreenVideo != src;
            _fullscreenVideo = entering ? src : null;
            WindowCommandRequested?.Invoke(entering
                ? Interaction.WindowCommand.EnterFullscreen
                : Interaction.WindowCommand.ExitFullscreen);
            Refresh();
            return true;
        }
        if (!_videoPlayers.TryGetValue(src, out var player)) return false;

        switch (cmd)
        {
            case "mute": player.Muted = !player.Muted; break;
            case "play": player.Play(); break;
            case "pause": player.Pause(); break;
            default: if (player.Playing) player.Pause(); else player.Play(); break;
        }
        Refresh(); // the controls' glyphs + labels reflect the new state
        return true;
    }

    /// <summary>The host's fullscreen state changed OUTSIDE the engine's own commands — the
    /// browser's Esc key ends fullscreen without the engine ever seeing a keystroke. Hosts call
    /// this from their fullscreen-change notification; leaving fullscreen releases any
    /// element-fullscreened video back into the layout.</summary>
    public void NotifyHostFullscreen(bool active)
    {
        if (active || _fullscreenVideo is null) return;
        _fullscreenVideo = null;
        Refresh();
    }

    /// <summary>True (once, then reset) if a background image load finished — or a live surface
    /// published a frame / (un)registered — since the last call. A render-on-demand host repaints
    /// when this returns true, so an async image appears and a paused video shows its seek frame.
    /// (Single pipe on purpose: both sides must consume their flag every poll.) Also the UI-thread
    /// reaction point for a video reaching its end (raised on the player's thread): the rebuild
    /// here flips its controls back to "Play".</summary>
    public bool ConsumeImageArrived()
    {
        if (System.Threading.Interlocked.Exchange(ref _videoStateChanged, 0) == 1)
        {
            Refresh();
            return true;
        }
        // The seek bar's clocks and fill live in the DOM, which only changes on rebuilds — but
        // Position advances continuously while playing (frames ride the fast path and rebuild
        // nothing). Reflect it about once a second: one ordinary rebuild per second per playing
        // video, with the fast path carrying every frame in between.
        foreach (var (src, player) in _videoPlayers)
        {
            if (!player.Playing) continue;
            var pos = player.Position;
            if (Math.Abs(pos - _reflectedPositions.GetValueOrDefault(src, -10)) < 0.95) continue;
            _reflectedPositions[src] = pos;
            Refresh();
            return true;
        }
        // A loaded IMAGE changes the display list (DrawImage captures at build time) — invalidate
        // the fast path. A live-surface frame does NOT (DrawSurface resolves at raster time):
        // that's exactly the case the fast path exists for, so it must not bump.
        var image = _images.TakeArrived();
        if (image) _inputsVersion++;
        return image | Surfaces.TakeArrived();
    }

    private readonly Dictionary<string, double> _reflectedPositions = new(StringComparer.Ordinal);

    public RenderNode Root => _root;

    /// <summary>Dev aid: outline every element's box on top of the paint (scroll containers in blue),
    /// to inspect layout in the live window. Off by default.</summary>
    public bool DebugOverlay { get => _painter.DebugOutline; set { _painter.DebugOutline = value; _inputsVersion++; } }

    /// <summary>Parse an HTML document and an optional external stylesheet.</summary>
    public static CupriDocument Load(string html, string? css = null)
    {
        var doc = new CupriDocument(html, css);
        doc.Rebuild();
        return doc;
    }

    /// <summary>Register a component library; custom elements expand during build (§10).</summary>
    public CupriDocument UseComponents(ComponentRegistry components)
    {
        _components = components;
        _cachedRules = null; // component CSS changes the rule set — rebuild the cache
        _cachedKeyframes = null;
        Rebuild();
        return this;
    }

    /// <summary>Bind a model; interpolations and <c>data-repeat</c> resolve against it.</summary>
    public CupriDocument Bind(object model)
    {
        _model = model;
        Rebuild();
        return this;
    }

    /// <summary>Register a font from raw TTF/OTF/TTC or WOFF 1 bytes (e.g. an embedded resource).
    /// Registered faces are consulted before platform fonts, and the first registered family becomes
    /// the target of the generic families (<c>sans-serif</c> etc.) — essential in the browser, where
    /// the wasm Skia build embeds only a monospace face. Register each style you use (Regular, Bold, …)
    /// before rendering. A stylesheet's <c>@font-face</c> rules do the same thing declaratively.</summary>
    public CupriDocument LoadFont(byte[] fontData)
    {
        _fonts.RegisterFont(fontData);
        return this;
    }

    /// <summary>Register a font from a <see cref="CupriSource"/> — embedded resource, file, or URL.</summary>
    public CupriDocument LoadFont(CupriSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _fonts.RegisterFont(source.ReadBytes());
        return this;
    }

    /// <summary>Register a font by the same kind of <c>src</c> an image takes: a bare embedded-resource
    /// path (resolved against the <see cref="UseImages"/> assembly), a file path or <c>file:</c> URL, a
    /// <c>data:</c> URI, or an <c>https:</c> URL under the image URL policy.</summary>
    public CupriDocument LoadFont(string src)
    {
        var bytes = Resources.SourceResolver.Load(src, _sourceAssembly, _images.UrlOptions)
                    ?? throw new Resources.CupriResourceException($"Font source '{src}' could not be loaded.");
        _fonts.RegisterFont(bytes);
        return this;
    }

    /// <summary>Register every font file in a directory (<c>.ttf</c>, <c>.otf</c>, <c>.ttc</c>,
    /// <c>.woff</c>; a <c>.woff2</c> is noted in <see cref="FontReport"/> rather than loaded). The
    /// files' own family/weight/style names are used — the way an OS installs a fonts folder.</summary>
    public CupriDocument LoadFonts(string directory, bool recursive = false)
    {
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(directory, "*.*", opt)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".woff" or ".woff2")
            .OrderBy(f => f, StringComparer.Ordinal); // a stable order: the first family registered becomes the sans default
        foreach (var file in files)
        {
            try { _fonts.RegisterFont(File.ReadAllBytes(file)); }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                _fontProblems.Add(new FontFaceProblem(Path.GetFileName(file), [file], ex.Message));
            }
        }
        return this;
    }

    /// <summary>See <see cref="Text.FontPolicy"/>. Set <see cref="Text.FontPolicy.RegisteredOnly"/> for
    /// output that must not depend on the machine (the same layout on every platform; the same
    /// pixels within one).</summary>
    public FontPolicy FontPolicy
    {
        get => _fonts.Policy;
        set => _fonts.Policy = value;
    }

    /// <summary>What the document's text resolved to, and any <c>@font-face</c> or font file that
    /// could not be loaded. Under <see cref="Text.FontPolicy.Platform"/> the resolutions marked
    /// <see cref="FontSource.Platform"/> or <see cref="FontSource.Default"/> are the machine-dependent ones.</summary>
    public FontReport FontReport
    {
        get { EnsureFontFaces(); return new(_fonts.Resolutions, _fonts.RegisteredFamilies, _fontProblems.ToList()); }
    }

    /// <summary>Remote image loads still in flight. Zero means every frame from now on is complete.</summary>
    public int PendingLoads => _images.PendingCount;

    /// <summary>True when no resource load is pending — the frame a render would produce now has
    /// nothing missing from it.</summary>
    public bool IsLoaded => _images.PendingCount == 0;

    private readonly List<FontFaceProblem> _fontProblems = new();
    private List<FontFaceRule>? _fontFaceRules;   // parsed with the cached rules
    private bool _fontFacesLoaded = true;         // false while parsed rules await the first layout
    private readonly HashSet<string> _loadedFaces = new(StringComparer.Ordinal); // family+url, so a rule re-cache does not re-register

    // Text is measured in layout, so this is the last moment a face can arrive; every layout entry
    // point calls it. A no-op after the first time (and always, for a document with no @font-face).
    private void EnsureFontFaces()
    {
        if (_fontFacesLoaded) return;
        _fontFacesLoaded = true;
        LoadFontFaces();
    }

    private void LoadFontFaces()
    {
        foreach (var rule in _fontFaceRules ?? [])
        {
            if (!_loadedFaces.Add(rule.Family + "\n" + string.Join("\n", rule.Sources.Select(x => x.Url)))) continue;
            Exception? last = null;
            var loaded = false;
            foreach (var src in rule.Sources)
            {
                try
                {
                    var bytes = Resources.SourceResolver.Load(src.Url, _sourceAssembly, _images.UrlOptions);
                    if (bytes is null) { last = new Resources.CupriResourceException($"'{src.Url}' could not be loaded."); continue; }
                    _fonts.RegisterFont(bytes, rule.Family, rule.WeightMin, rule.WeightMax, rule.Slant);
                    loaded = true;
                    break;
                }
                catch (Exception ex) { last = ex; }
            }
            if (loaded) continue;
            var problem = new FontFaceProblem(rule.Family, rule.Sources.Select(x => x.Url).ToList(), last?.Message ?? "no url() source");
            _fontProblems.Add(problem);
            // Under Platform the family falls to the machine, as it would in a browser; under
            // RegisteredOnly there is nothing to fall to, so say so now rather than at first paint.
            if (_fonts.Policy == FontPolicy.RegisteredOnly)
                throw new Resources.CupriResourceException($"@font-face \"{rule.Family}\" could not be loaded ({problem.Reason}), and the font policy is RegisteredOnly.", last);
        }
    }

    /// <summary>Re-apply bindings with the current model (call after model changes).</summary>
    public void Refresh() => Rebuild();

    /// <summary>Optional diagnostic hook: invoked with (phaseName, ms) for each Rebuild phase.</summary>
    public static Action<string, double>? ProfileHook;

    private void Rebuild()
    {
        _inputsVersion++; // a fresh DOM invalidates the fast path's retained display list
        var prof = ProfileHook;
        var t = prof is not null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        void Mark(string phase)
        {
            if (prof is null) return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            prof(phase, System.Diagnostics.Stopwatch.GetElapsedTime(t, now).TotalMilliseconds);
            t = now;
        }

        // A rebuild starts from a fresh DOM, so scroll offsets on the fresh tree would reset — carry
        // them over (keyed by structural path, since element identity isn't stable across rebuilds).
        var scroll = CaptureScroll();
        CancelOrphanedPointerDrags(); // any node the pointer was dragging is about to be orphaned

        _dom?.Dispose();
        // Parse the (immutable) template ONCE; every rebuild — each keystroke — deep-clones the parsed
        // DOM instead of re-tokenizing the HTML string, which is severalfold cheaper.
        _templateDom ??= new HtmlParser().ParseDocument(_templateHtml);
        var dom = (IDocument)_templateDom.Clone();
        Mark("parse-html");

        if (_model is not null)
            BindingEngine.Apply(dom, _model,                              // window each <cupri-virtual>
                key => new BindingEngine.VirtualListState(
                    _virtualScroll.GetValueOrDefault(key),
                    _virtualHeights.GetValueOrDefault(key),
                    _virtualAtBottom.GetValueOrDefault(key),
                    Known: _virtualScroll.ContainsKey(key) || _virtualAtBottom.ContainsKey(key)),
                (key, y) => { _pendingVirtualScroll[key] = y; _virtualPinned.Add(key); });
        Mark("bind");

        // Expand custom elements after binding so components see concrete attribute values.
        _components?.Expand(dom);
        SyncVideos(dom); // players follow the DOM: open for new sources, label controls, retire gone ones
        // …and the same moment, opened up. A surface producer outside this assembly — a Lottie player,
        // and the 3D viewport or camera SurfaceRegistry already anticipates — needs exactly what
        // SyncVideos needs: the expanded DOM, every rebuild, so it can open sources that appeared and
        // retire ones whose element is gone. Without it an optional package can ship a component that
        // renders nothing, because nothing ever told it the element exists.
        if (_rebuiltHandlers is { Count: > 0 })
            foreach (var h in _rebuiltHandlers) h(dom);
        Mark("expand-components");

        // Re-apply text focus across the rebuild (typing rebuilds the DOM each keystroke), and
        // paint the raw edit buffer (which may be invalid) over the bound value, flagging
        // data-invalid so the field can show a red border while the user is mid-edit.
        var focusEl = _focusKey is not null ? dom.QuerySelector($"[data-bind-value=\"{_focusKey}\"]") : null;

        // Auto-focus a field that asked for it (e.g. a command palette's search) when no text field is
        // already focused — so opening the palette lands the caret in the search box, ready to type. Only
        // present while its overlay is open, so it never steals focus otherwise.
        if (focusEl is null && dom.QuerySelector("[data-autofocus][data-bind-value]") is { } af)
        {
            _focusKey = af.GetAttribute("data-bind-value");
            _editBuffer = null;
            var qlen = (_model is not null ? BindingEngine.Resolve(_model, _focusKey!)?.ToString() : null)?.Length ?? 0;
            _caret = _selAnchor = qlen; // caret at the end of any existing text
            focusEl = af;
        }

        if (focusEl is not null)
        {
            focusEl.SetAttribute("data-focus", "");
            if (_editBuffer is not null)
            {
                // Only overwrite the value text when the buffer is non-empty: blanking a text
                // node removes it (no line box → the field collapses and its rounded corners
                // read as a pill). An empty buffer keeps the component's own content — e.g. the
                // placeholder for a text field stays visible until the user types.
                if (_editBuffer.Length > 0)
                {
                    var anchor = focusEl.QuerySelector("[data-caret-anchor]") ?? focusEl;
                    if (focusEl.HasAttribute("data-multiline"))
                        anchor.InnerHtml = Components.Controls.TextAreaComponent.RenderLines(_editBuffer);
                    else // mask secret fields (data-mask): paint bullets, keep the plaintext in the buffer
                        anchor.TextContent = MaskText(_editBuffer);
                }
                if (!BufferValid(_editBuffer)) focusEl.SetAttribute("data-invalid", "");
            }
        }

        Mark("focus");

        // The context menu is engine-owned, so re-inject it on every rebuild (the DOM re-parses
        // from the template each time) while it's open — same approach as focus re-application.
        if (_ctxOpen) InjectContextMenu(dom);
        if (_ctxCustomIndex >= 0) RevealCustomContextMenu(dom); // a <cupri-context-menu>'s popup, opened at the pointer
        if (_toasts.Count > 0) InjectToaster(dom);              // the engine-owned toast stack (doc.Toast)

        // CSS rules + @keyframes come from immutable sources (component CSS, author CSS, template
        // <style> tags), so parse them ONCE and reuse across rebuilds — a rebuild happens on every
        // click/keystroke, and re-parsing + re-allocating the rule set each time was pure waste.
        if (_cachedRules is null)
        {
            var rules = new List<CssRule>();
            if (_components is not null) rules.AddRange(CssParser.Parse(_components.AggregatedCss));
            rules.AddRange(CssParser.Parse(_css));
            foreach (var styleEl in dom.QuerySelectorAll("style"))
                rules.AddRange(CssParser.Parse(styleEl.TextContent));
            for (var i = 0; i < rules.Count; i++) rules[i].Order = i; // later stylesheets win ties

            var kf = Animation.Parse(_css);
            if (_components is not null)
                foreach (var (k, frames) in Animation.Parse(_components.AggregatedCss)) kf[k] = frames;
            foreach (var styleEl in dom.QuerySelectorAll("style"))
                foreach (var (k, frames) in Animation.Parse(styleEl.TextContent)) kf[k] = frames;

            // @font-face rules: same immutable sources, same one-time parse. They are LOADED at the
            // first layout rather than here: Load() rebuilds before UseImages has named the assembly
            // an embedded font resolves against, and before the app has chosen a font policy.
            var faceCss = new List<string?> { _css };
            if (_components is not null) faceCss.Add(_components.AggregatedCss);
            foreach (var styleEl in dom.QuerySelectorAll("style")) faceCss.Add(styleEl.TextContent);
            _fontFaceRules = faceCss.SelectMany(FontFace.Parse).ToList();
            _fontFacesLoaded = _fontFaceRules.Count == 0;

            _cachedRules = rules;
            _cachedKeyframes = kf;
            _hasMedia = rules.Exists(r => r.Media is not null);
        }
        _rules = _cachedRules;
        _keyframes = _cachedKeyframes!;
        ApplyInputProfile(dom);
        Mark("parse-css");

        ApplyValidation(dom); // inject inline error messages before the tree is built
        Mark("validate");

        _hoverChain.Clear();
        _activeChain.Clear();   // same reason as hover: the fresh DOM carries no data-active, and a
                                // stale chain would hand ClearActive() dead elements on pointer-up
        var resolver = new StyleResolver(_rules, _viewportWidth, _viewportHeight);
        _root = resolver.BuildTree(dom);
        _hasViewportUnits |= resolver.SawViewportUnit;
        _layoutDirty = true; // fresh tree: no geometry until the next layout
        RestoreScroll(scroll);
        // Scroll offsets the BINDER chose (a bottom-anchored list opening at / following its tail,
        // or a prepend's compensation): the node exists only now. Overrides whatever RestoreScroll
        // carried, and syncs _virtualScroll so the next wheel tick measures drift against the same
        // number instead of immediately re-windowing.
        foreach (var (vkey, vy) in _pendingVirtualScroll)
        {
            if (FindByVirtualKey(_root, vkey) is { } vn) vn.ScrollY = (float)vy;
            _virtualScroll[vkey] = vy;
        }
        _pendingVirtualScroll.Clear();
        _dom = dom;
        if (float.IsFinite(_lastHitX) && float.IsFinite(_lastHitY))
        {
            // A model refresh replaces every DOM element. Re-hit-test the last pointer position on the
            // fresh tree so :hover does not blink off until the mouse moves again. This matters most for
            // periodically refreshed dashboards: otherwise each refresh clears hover, and every small
            // pointer movement restores it, repeatedly restarting hover transitions under a still cursor.
            EnsureLaidOut();
            _ = UpdateHover(_lastHitX, _lastHitY);
        }
        else
        {
            _transitions.Detect(_root, _laidOutWidth, _laidOutHeight); // (re)start transitions whose target value changed this rebuild
        }
        Mark("style+tree");
        _hasActiveAnim = _keyframes.Count > 0 && AnyAnimated(_root);
        _animRunning = true; // until Animate says the last finite animation has ended
    }

    // Per-node interaction state preserved across a rebuild, keyed by structural path (child-index
    // chain from root) since the DOM — and thus element identity — is re-parsed each rebuild.
    private readonly record struct NodeState(float ScrollY, bool AtBottom, bool FollowTail, float? ResizeW, float? ResizeH, float ScrollX, float NaturalHeight, float DisplayHeight, float? SplitGrow);

    // A height transition needs the element's natural (auto) height at Detect time, which runs on the
    // freshly-rebuilt tree before it's laid out — so carry the last measured value across the rebuild.
    private static bool HasHeightTransition(Style.ComputedStyle s)
    {
        if (s.Transitions is not { Count: > 0 } specs) return false;
        foreach (var sp in specs) if (sp.Property is "height" or "all") return true;
        return false;
    }

    private Dictionary<string, NodeState>? CaptureScroll()
    {
        if (_root is null) return null;
        Dictionary<string, NodeState>? map = null;
        void Walk(RenderNode n)
        {
            var tail = n.Element?.HasAttribute("data-follow-tail") == true;
            // EVERY scroller is recorded, including one sitting at 0. The memory below outlives the
            // tree, so an unrecorded scroller would keep whatever offset it had when it vanished —
            // "scrolled back to the top" has to be remembered just as firmly as "scrolled down".
            var isScroller = n.Style.Overflow == OverflowMode.Scroll;
            var scroll = isScroller && (n.ScrollY > 0.01f || tail);
            // A node not laid out this cycle (a rebuild landed before the next layout) has a stale 0
            // height — carry its last real displayed height (PrevHeight) forward instead, so a height
            // transition doesn't think an open panel collapsed to nothing.
            var displayH = n.LaidOut ? n.Height : n.PrevHeight;
            // Carry for ANY on-screen height (not only measured-natural content): a definite-height
            // element — the video card between its size presets — has no flow content, so natural
            // height stays 0; without the carry its transition would animate up from zero.
            var hTrans = (n.ContentNaturalHeight > 0 || displayH > 0) && HasHeightTransition(n.Style);
            if (scroll || isScroller || n.ResizeW is not null || n.ResizeH is not null || n.ScrollX > 0.01f || hTrans || n.SplitGrow is not null)
            {
                var state = new NodeState(n.ScrollY, n.ScrollY >= n.MaxScrollY - 1f, tail, n.ResizeW, n.ResizeH, n.ScrollX, n.ContentNaturalHeight, displayH, n.SplitGrow);
                (map ??= new())[PathOf(n)] = state;
                _scrollMemory[PathOf(n)] = state;
            }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        return map;
    }

    /// <summary>What kind of input this app is being driven by. Hosts set it (a phone is coarse and
    /// hoverless, a desktop window is fine and hovers); apps may override it. The engine's only job
    /// is to put it on the body as classes, so ordinary CSS can respond:
    /// <c>cupri-coarse</c>/<c>cupri-fine</c> and <c>cupri-nohover</c>.
    ///
    /// Deliberately NOT a media feature. `@media (pointer: coarse)` would mean teaching the CSS
    /// parser a discrete-keyword shape it does not have (it matches numeric px lengths), to reach
    /// exactly what the cascade already does with a class — and a class works identically for app
    /// stylesheets and for a component's own DefaultCss.</summary>
    public InputProfile InputProfile
    {
        get => _inputProfile;
        set
        {
            if (_inputProfile == value) return;
            _inputProfile = value;
            if (_dom is not null) { ApplyInputProfile(_dom); Refresh(); }
        }
    }
    private InputProfile _inputProfile = InputProfile.Desktop;

    private void ApplyInputProfile(AngleSharp.Dom.IDocument dom)
    {
        if (dom.Body is not { } body) return;
        body.ClassList.Remove("cupri-coarse", "cupri-fine", "cupri-nohover");
        body.ClassList.Add(_inputProfile.CoarsePointer ? "cupri-coarse" : "cupri-fine");
        if (!_inputProfile.Hover) body.ClassList.Add("cupri-nohover");
    }

    private void RestoreScroll(Dictionary<string, NodeState>? map)
    {
        void Walk(RenderNode n)
        {
            // The capture map only sees what was in the LAST tree, and `display:none` builds no
            // children (StyleResolver) — so a page that was hidden when the rebuild happened
            // contributed nothing, and its scroller would come back at zero. That is what left the
            // virtualised list blank on return: the window (kept per virtual key, which does
            // survive) still described row 15 while the offset had been reset to 0, so the rows
            // were laid out past the bottom of an unscrolled viewport. The memory outlives the
            // tree, so a page restores where the user left it.
            var path = PathOf(n);
            if ((map is not null && map.TryGetValue(path, out var s)) || _scrollMemory.TryGetValue(path, out s))
            {
                if (n.Style.Overflow == OverflowMode.Scroll)
                    n.ScrollY = s.FollowTail && s.AtBottom ? float.MaxValue : s.ScrollY; // MaxValue → new bottom (tail-follow)
                n.ResizeW = s.ResizeW;
                n.ResizeH = s.ResizeH;
                n.ScrollX = s.ScrollX;
                if (s.NaturalHeight > 0) n.ContentNaturalHeight = s.NaturalHeight; // seed a height transition's auto target
                n.PrevHeight = s.DisplayHeight;                                     // …and the height it animates from
                n.SplitGrow = s.SplitGrow;                                          // …and a dragged split ratio
            }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
    }

    private static string PathOf(RenderNode n)
    {
        var sb = new System.Text.StringBuilder();
        for (var cur = n; cur.Parent is { } p; cur = p) sb.Insert(0, "/" + p.Children.IndexOf(cur));
        return sb.ToString();
    }

    /// <summary>Advance @keyframes animations and in-flight CSS transitions to the given elapsed time.
    /// Mostly paint-only; a <c>transition: height</c> — or a width/height <c>@keyframes</c> — writes a
    /// definite size that the following layout honours, so the element and its siblings reflow.
    /// Returns true if anything animated this frame.</summary>
    public bool Animate(double timeSeconds)
    {
        var any = false;
        // Advance the toast stack first: a phase flip rebuilds and Detects a new opacity/transform target,
        // so the transition below applies it the SAME frame — the toast starts from its off-screen state
        // instead of flashing at the target for one frame before the transition kicks in.
        if (_toasts.Count > 0 && StepToasts(timeSeconds)) any = true;
        // @keyframes RULES existing is not animation HAPPENING: hosts call Animate whenever rules
        // exist (HasAnimations), but only a visibly animated node makes this frame's output differ.
        if (_keyframes.Count > 0)
        {
            // A finite animation that has run its iterations stops driving frames (a `forwards`
            // fill holds its last frame); a host polling HasActiveAnimations goes idle with it.
            _animRunning = Animation.Apply(_root, _keyframes, timeSeconds);
            if (_hasActiveAnim && _animRunning) any = true;
        }
        if (_transitions.Apply(_root, timeSeconds)) any = true; // interpolate transitions over @keyframes
        if (_maskRevealPos >= 0) // a masked field is peeking its last-typed char — time it out
        {
            if (double.IsNaN(_maskRevealStart)) _maskRevealStart = timeSeconds;      // stamp on the first frame
            else if (timeSeconds - _maskRevealStart >= MaskPeekSeconds)              // window elapsed → re-mask
            {
                _maskRevealPos = -1; _maskRevealStart = double.NaN;
                Rebuild(); // re-bake the field text fully masked (paint-only content swap)
            }
            any = true;
        }
        if (EaseReorder(timeSeconds)) any = true; // slide reorder rows into their gap
        if (StepFling(timeSeconds)) any = true;   // momentum scrolling (touch fling)
        if (StepOverscroll(timeSeconds)) any = true;   // the rubber band springing back
        // Animation wrote style/offset values this frame → the retained fast-path list is stale.
        // A quiet call (rules exist, nothing active — every page of an app with a spinner
        // somewhere) must NOT bump, or the surface fast path would never fire anywhere.
        if (any) _inputsVersion++;
        return any;
    }

    // True while a masked field is peeking its last-typed char — keeps the host's frame pump alive
    // (folded into both continuous-render signals below) so Animate() can time the peek out.
    private bool MaskPeeking => _maskRevealPos >= 0;

    public bool HasAnimations => _keyframes.Count > 0;

    /// <summary>True while a CSS transition is mid-flight (a continuous host should keep calling
    /// <see cref="Animate"/> and repainting until it settles). Also true while a masked field is
    /// peeking its last-typed char, so the host keeps ticking until <see cref="Animate"/> re-masks it.</summary>
    public bool HasActiveTransitions => _transitions.Active || MaskPeeking || ReorderEasing || ToastsPending || FlingActive || OverscrollActive;

    /// <summary>True only if a *visible* node is currently animating (display:none subtrees are
    /// absent from the render tree). Lets a host render continuously only when it must, instead
    /// of every frame — critical for the CPU-rendered web host. Cached per rebuild (the animated
    /// set only changes when the tree does), so a host may poll it every frame for free. Also true
    /// while a masked field peeks its last-typed char (see <see cref="HasActiveTransitions"/>),
    /// and while any live surface (a playing video) is producing frames.</summary>
    public bool HasActiveAnimations => (_hasActiveAnim && _animRunning) || _transitions.Active || MaskPeeking || ReorderEasing || ToastsPending || Surfaces.AnyTicking || FlingActive || OverscrollActive;
    private bool _hasActiveAnim;
    private bool _animRunning = true;

    private static bool AnyAnimated(RenderNode n)
    {
        if (n.Style.AnimationName is not null && n.Style.AnimationDuration > 0) return true;
        foreach (var c in n.Children) if (AnyAnimated(c)) return true;
        return false;
    }

    // ---- page zoom ---------------------------------------------------------------------------
    // REFLOW zoom, which is what a browser's Ctrl+= does and what accessibility guidance asks for:
    // the document lays out at size/Zoom and paints scaled up, so content reflows into the narrower
    // viewport instead of forcing the reader to scroll sideways. A magnifier (fixed layout, pan
    // around a bigger surface) would be the other choice and is deliberately NOT this: reflow is
    // what keeps a zoomed page readable in one column.
    //
    // It lives in the engine rather than in each host so every platform gets it at once. A host
    // keeps passing window-logical coordinates; everything crossing that boundary is divided by
    // Zoom on the way in, and the semantics tree's bounds are multiplied on the way out.

    /// <summary>The narrowest and widest the page may be scaled to.</summary>
    public const float MinZoom = 0.5f, MaxZoom = 4f;

    private float _zoom = 1f;

    /// <summary>Whole-document zoom, 1 = unzoomed. Clamped to
    /// <see cref="MinZoom"/>..<see cref="MaxZoom"/>. Layout happens at viewport/Zoom, so raising it
    /// makes everything bigger AND reflows the content to the narrower width — <c>@media</c>
    /// queries see that narrower width, exactly as a browser's page zoom behaves.
    ///
    /// <para>Settable before the first frame, which is how an app restores a remembered level:
    /// <c>DesktopHost.Run(app, doc =&gt; doc.Zoom = LoadMyZoom())</c> — the host's configure hook
    /// runs after the document is built and before anything is laid out, so the first frame the
    /// user sees is already at their zoom, with no visible jump from 1.</para></summary>
    public float Zoom
    {
        get => _zoom;
        set
        {
            var z = Math.Clamp(value, MinZoom, MaxZoom);
            if (MathF.Abs(z - _zoom) < 0.0005f) return;
            _zoom = z;
            _layoutDirty = true;
            _lastPresented = null;   // the retained damage list describes the old scale
            _lastList = null;
            Bump(true);
            ZoomChanged?.Invoke(z);
        }
    }

    /// <summary>Raised after <see cref="Zoom"/> settles at a new level, carrying the CLAMPED value —
    /// so what an app stores is exactly what it can hand back.
    ///
    /// <para><b>Persistence is the app's, deliberately.</b> The engine has no idea where this app
    /// keeps settings, and a UI toolkit that picks a file path for you is a toolkit you have to
    /// fight later. Subscribe and store it wherever the rest of your preferences live:</para>
    /// <code>
    /// doc.Zoom = Prefs.Zoom;              // restore before the first frame
    /// doc.ZoomChanged += z => Prefs.Zoom = z;   // remember every later change
    /// </code>
    /// <para>This fires for EVERY route to a new level — a pinch, Ctrl/Cmd +/−/0, Ctrl+wheel, or an
    /// assignment — because the user-driven ones are exactly the changes an app cannot otherwise
    /// see. It does NOT fire when a set lands on the level already in force (including a clamp that
    /// collapses onto the current one), so a naive handler that writes to disk will not be called
    /// in a loop by a user holding a zoom key at the limit.</para></summary>
    public event Action<float>? ZoomChanged;

    // The browser's zoom ladder. Discrete steps rather than a multiplier so that repeated in/out
    // always returns exactly home (no 1×1.1÷1.1 drift), the jumps feel like a browser's, and the
    // ends are the same MinZoom/MaxZoom the pinch obeys.
    private static readonly float[] ZoomSteps =
        { 0.5f, 0.67f, 0.75f, 0.8f, 0.9f, 1f, 1.1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f, 4f };

    /// <summary>One ladder step larger — a host's Ctrl/Cmd+= (or Ctrl+wheel-up). From a value the
    /// ladder doesn't contain (a pinch can leave 1.37) it climbs to the next rung above. Honours
    /// <see cref="PageZoomEnabled"/>, because it is the same affordance by another input.</summary>
    public void ZoomIn() { if (PageZoomEnabled) Zoom = StepFrom(_zoom, +1); }

    /// <summary>One ladder step smaller — Ctrl/Cmd+− (or Ctrl+wheel-down).</summary>
    public void ZoomOut() { if (PageZoomEnabled) Zoom = StepFrom(_zoom, -1); }

    /// <summary>One step larger, keeping whatever sits under (<paramref name="hostX"/>,
    /// <paramref name="hostY"/>) where it is — Ctrl+wheel, which happens at a pointer.</summary>
    public void ZoomIn(float hostX, float hostY) => ZoomStepAnchored(+1, hostX, hostY);

    /// <summary>One step smaller, anchored at the pointer.</summary>
    public void ZoomOut(float hostX, float hostY) => ZoomStepAnchored(-1, hostX, hostY);

    // Zoom a step and then scroll so the thing the user was pointing at has not moved.
    //
    // The anchor is an ELEMENT, not a coordinate, and that is forced by what this feature IS. A
    // magnifier could keep a pixel still, but this reflows: at a new zoom the text rewraps and
    // every coordinate below the change means something else, so "the point that was at y=400"
    // is not a promise the engine can keep. "The paragraph you were pointing at" is — re-found
    // after the reflow by structural path, the same way flings and focus survive a rebuild.
    private void ZoomStepAnchored(int dir, float hostX, float hostY)
    {
        if (!PageZoomEnabled) return;
        var target = StepFrom(_zoom, dir);
        if (MathF.Abs(target - _zoom) < 0.0005f) return;   // already at the end of the ladder

        EnsureLaidOut();
        var before = _zoom;
        var anchor = HitTesting.HitTest(_root, Zc(hostX), Zc(hostY));
        // Anchoring means scrolling something, so both the anchor and the scroller that carries it
        // have to survive the reflow; without a scroller there is nothing to correct with.
        var scroller = anchor;
        while (scroller is not null && !scroller.IsScrollable) scroller = scroller.Parent;
        var anchorPath = anchor is null ? null : PathOf(anchor);
        var scrollPath = scroller is null ? null : PathOf(scroller);
        var wasAtHost = anchor is null ? 0 : PaintedTopLeft(anchor).Y * before;   // host px, pre-zoom

        Zoom = target;
        if (anchorPath is null || scrollPath is null) return;

        EnsureLaidOut();                                    // reflow at the new level
        if (NodeAtPath(anchorPath) is not { } again || NodeAtPath(scrollPath) is not { IsScrollable: true } sc)
            return;                                         // the anchor did not survive; leave the scroll alone

        // Painted top in host pixels now, versus where it sat before. Scrolling by d moves painted
        // content up by d, so the correction is simply the difference.
        var nowAtHost = PaintedTopLeft(again).Y * _zoom;
        var delta = (nowAtHost - wasAtHost) / _zoom;        // back into document px, which scroll is in
        sc.ScrollY = Math.Clamp(sc.ScrollY + delta, 0, sc.MaxScrollY);
        Bump(true);
    }

    /// <summary>Back to 1:1 — Ctrl/Cmd+0. Not gated: whatever zoomed the page, undoing it must
    /// always be available.</summary>
    public void ZoomReset() => Zoom = 1f;

    private static float StepFrom(float current, int dir)
    {
        if (dir > 0)
        {
            foreach (var s in ZoomSteps) if (s > current + 0.0005f) return s;
            return MaxZoom;
        }
        for (var i = ZoomSteps.Length - 1; i >= 0; i--) if (ZoomSteps[i] < current - 0.0005f) return ZoomSteps[i];
        return MinZoom;
    }

    /// <summary>Window-logical → document coordinates. Every public entry point that accepts a
    /// point from a host passes through this, so a zoomed page is still clicked where it looks.</summary>
    private float Zc(float v) => v / _zoom;

    /// <summary>Lay out at the given viewport size and paint onto <paramref name="canvas"/>.</summary>
    public void Render(SKCanvas canvas, float width, float height)
    {
        var list = BuildFrame(width, height);
        if (_zoom == 1f) { _rasterizer.Paint(canvas, list); return; }

        var restore = canvas.Save();
        canvas.Scale(_zoom);
        _rasterizer.Paint(canvas, list);
        canvas.RestoreToCount(restore);
    }

    // The previously-presented frame's commands, for RenderIncremental's damage diff. Only that path
    // writes these — Render/RenderToImage stay stateless full repaints.
    private IReadOnlyList<Paint.PaintCommand>? _lastPresented;
    private float _lastPresentedW, _lastPresentedH;

    // ---- the surface fast path ----------------------------------------------------------------
    // Everything that can change the rendered output EXCEPT a live surface swapping its frame
    // bumps _inputsVersion (dispatch shims, Rebuild, Animate, image arrivals). BuildFrame stamps
    // the version it was built from. When they match at render time, the retained display list is
    // still exact — a new video frame changed no command (DrawSurface resolves at raster time) —
    // so the frame costs one clipped raster of the video rect instead of layout+paint+diff of the
    // whole page (measured 3.49 ms → the ~0.3 ms floor on the Showcase).
    private int _inputsVersion = 1, _builtVersion;
    private DisplayList? _lastList; // the retained list the fast path re-rasters
    private readonly Dictionary<Paint.ISurfaceSource, SKImage?> _seenSurfaceFrames = new();
    // The culled replay for the fast path's damage rect — identical from frame to frame while the
    // video sits still, so cull once and reuse until the list or the rect changes.
    private IReadOnlyList<Paint.PaintCommand>? _cullCache;
    private SKRectI _cullCacheRect;
    private object? _cullCacheList;
    private DrawSurface[] _lastListSurfaces = []; // pre-extracted, so fast frames skip the full walk

    /// <summary>One user-perceived character: a single UTF-16 unit, or one surrogate pair.</summary>
    private static bool IsSingleCodePoint(string s) =>
        s.Length == 1 || (s.Length == 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1]));

    private bool Bump(bool changed)
    {
        if (changed) _inputsVersion++;
        return changed;
    }

    /// <summary>Monotonic counter of input-driven content changes (dispatches, rebuilds, animation
    /// writes, image arrivals) — live-surface frame swaps deliberately excluded. Lets a host skip
    /// work that depends only on semantics/layout, e.g. re-publishing an accessibility snapshot
    /// 60×/s while a video merely plays.</summary>
    public int ContentVersion => _inputsVersion;

    // Union into `damage` the box of every DrawSurface whose frame changed since the last render,
    // remembering the new references. The damage DIFF can't see those changes — two DrawSurface
    // records with the same source and geometry compare equal by design. `prune` (the normal,
    // freshly-built path) also drops remembered sources that left the list, so retired players
    // don't accumulate. A first-sight source unions its rect too — on the normal path the diff
    // already covers it (harmless double-union), and the fast path can't meet one.
    /// <summary>Map a damage rectangle returned by <see cref="RenderIncremental"/> into DEVICE pixels,
    /// for a host that applied its OWN scale to the canvas before calling it — a HiDPI present, or an
    /// authored design size fitted to the viewport. Such a host renders the document at logical size
    /// and scales the raster, so the rectangle comes back in logical units and the region it must
    /// re-upload is that rectangle scaled.
    ///
    /// <para>Rounds OUTWARD and clamps to the surface, for the reasons in <c>DeviceRect</c>: short by
    /// a fraction of a device pixel leaves a stale seam, past the edge is an out-of-bounds upload.
    /// Identity at <paramref name="scale"/> 1, so a host can call it unconditionally (#99).</para></summary>
    public static SKRectI ScaleDamageToDevice(SKRectI logical, float scale, int deviceWidth, int deviceHeight)
    {
        var left = Math.Max(0, (int)MathF.Floor(logical.Left * scale));
        var top = Math.Max(0, (int)MathF.Floor(logical.Top * scale));
        var right = Math.Min(deviceWidth, (int)MathF.Ceiling(logical.Right * scale));
        var bottom = Math.Min(deviceHeight, (int)MathF.Ceiling(logical.Bottom * scale));
        return right <= left || bottom <= top ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

    /// <summary>Map a DOCUMENT-space damage rectangle to the DEVICE rectangle a host re-uploads.
    /// Zoom is a uniform <c>canvas.Scale</c> — no rotation, no skew — so this is an exact multiply.
    /// The care is all in rounding OUTWARD: floor the minimum and ceil the maximum, because a
    /// rectangle short by a fraction of a device pixel leaves a stale seam on screen, while one that
    /// is a pixel too generous costs a pixel. Clamped to the surface, since the result is an upload
    /// region and an out-of-bounds one is a host-side fault (#99).</summary>
    private SKRectI DeviceRect(SKRect doc, float width, float height)
    {
        var s = _zoom;
        var left = Math.Max(0, (int)MathF.Floor(doc.Left * s));
        var top = Math.Max(0, (int)MathF.Floor(doc.Top * s));
        var right = Math.Min((int)MathF.Ceiling(width), (int)MathF.Ceiling(doc.Right * s));
        var bottom = Math.Min((int)MathF.Ceiling(height), (int)MathF.Ceiling(doc.Bottom * s));
        return right <= left || bottom <= top ? SKRectI.Empty : new SKRectI(left, top, right, bottom);
    }

    private SKRect SurfaceDamage(IReadOnlyList<Paint.PaintCommand> cmds, SKRect damage, bool prune)
    {
        List<Paint.ISurfaceSource>? present = prune ? new() : null;
        foreach (var cmd in cmds)
        {
            if (cmd is not DrawSurface ds) continue;
            present?.Add(ds.Source);
            var frame = ds.Source.CurrentFrame;
            if (_seenSurfaceFrames.TryGetValue(ds.Source, out var seen) && ReferenceEquals(seen, frame)) continue;
            _seenSurfaceFrames[ds.Source] = frame;
            var r = SKRect.Create(ds.X, ds.Y, ds.W, ds.H);
            damage = damage.IsEmpty ? r : SKRect.Union(damage, r);
        }
        if (present is not null && _seenSurfaceFrames.Count > present.Count)
        {
            List<Paint.ISurfaceSource>? drop = null;
            foreach (var k in _seenSurfaceFrames.Keys) if (!present.Contains(k)) (drop ??= new()).Add(k);
            if (drop is not null) foreach (var k in drop) _seenSurfaceFrames.Remove(k);
        }
        return damage;
    }

    /// <summary>The host's retained canvas lost its pixels (its backing surface was recreated —
    /// e.g. the SDL window rebuilt its bitmap+texture on a size change). The damage diff would
    /// otherwise keep assuming last frame's pixels are still on screen and repaint only what
    /// changed — into a blank surface, leaving everything else black. Dropping the diff base
    /// makes the next <see cref="RenderIncremental"/> a full repaint.</summary>
    public void InvalidateRetainedFrame() { _lastPresented = null; _lastList = null; }

    /// <summary>Where the last rendered frame's time went — the observability the video work runs
    /// on. Layout + paint-list come from <see cref="BuildFrame"/>; diff + raster + damage area
    /// from <see cref="RenderIncremental"/> (a full <see cref="Render"/> reports raster only).</summary>
    public readonly record struct FrameTimings(
        double LayoutMs, double PaintListMs, double DiffMs, double RasterMs, int DamagePixels,
        bool FastPath = false)
    {
        public double TotalMs => LayoutMs + PaintListMs + DiffMs + RasterMs;
    }

    /// <summary>Timings of the most recent rendered frame (see <see cref="FrameTimings"/>).</summary>
    public FrameTimings LastFrame { get; private set; }
    private double _tLayout, _tPaint; // BuildFrame's contribution to the frame being rendered

    /// <summary>Live playback internals of every open video player that reports any
    /// (<see cref="Media.IVideoPlayer.DiagnosticsSummary"/>) — one line per player, or null.</summary>
    public string? VideoDiagnostics
    {
        get
        {
            List<string>? lines = null;
            foreach (var p in _videoPlayers.Values)
                if (p.DiagnosticsSummary is { Length: > 0 } s) (lines ??= []).Add(s);
            return lines is null ? null : string.Join("\n", lines);
        }
    }

    /// <summary>Render for a host whose canvas RETAINS its pixels between frames (the SDL software
    /// bitmap, the WASM staging bitmap): diffs this frame's display list against the last one presented,
    /// clips the repaint to the damaged rectangle, and returns that rectangle — or <c>null</c> when the
    /// frame is identical, in which case nothing was drawn and the host can skip presenting entirely.
    /// The very first call (and any viewport size change) repaints in full. Only valid when every frame
    /// lands on the SAME retained canvas; one-shot renders should use <see cref="Render"/>.
    /// The result is visually identical to a full render; anti-aliased pixels of primitives crossing the
    /// damage boundary may differ from a monolithic render by a couple of least-significant bits (Skia
    /// computes AA coverage against the clip).</summary>
    public SKRectI? RenderIncremental(SKCanvas canvas, float width, float height, SKColor background)
    {
        // Damage is computed in DOCUMENT space and returned in DEVICE space; DeviceRect maps between
        // them. This used to give up and repaint in full whenever _zoom != 1, on the grounds that zoom
        // is "a deliberate, occasional act" — true of Ctrl+plus, false of a device pixel ratio, which
        // is 2 on a HiDPI screen and 1.25 or 1.5 on a fractionally-scaled desktop. Scale 1 is the
        // exception, so that bail-out cost damage tracking on most machines (#99).

        // FAST PATH: no input/rebuild/animation touched the document since the retained list was
        // built — only live-surface frames can differ. Re-raster the SAME list clipped to the
        // changed surfaces (DrawSurface resolves its frame at raster time); skip layout, paint
        // building and the diff entirely.
        if (_builtVersion == _inputsVersion && _lastList is { } retained && _lastPresented is not null
            && _lastPresentedW == width && _lastPresentedH == height)
        {
            var f0 = Stopwatch.GetTimestamp();
            var surface = SurfaceDamage(_lastListSurfaces, SKRect.Empty, prune: false);
            if (surface.IsEmpty)
            {
                LastFrame = new FrameTimings(0, 0, 0, 0, 0, FastPath: true);
                return null;                                   // no new frames either — clean
            }
            // TWO rectangles, and mixing them paints the wrong region: the cull is matched against
            // command bounds, which are in document space; the clip and the returned rect are what
            // the host re-uploads, which is device space.
            var srect = SKRectI.Ceiling(surface);
            var sdev = DeviceRect(surface, width, height);
            if (sdev.IsEmpty)
            {
                LastFrame = new FrameTimings(0, 0, 0, 0, 0, FastPath: true);
                return null;
            }
            // Replay only what intersects the rect: submitting the whole page for Skia to
            // clip-reject costs more than the video raster itself (measured ~2.3 ms of walk).
            // The culled list is stable while the video sits still — cull once, reuse.
            if (_cullCache is null || !ReferenceEquals(_cullCacheList, retained) || _cullCacheRect != srect)
            {
                _cullCache = Paint.DamageDiff.CullTo(retained.Commands, srect);
                _cullCacheList = retained;
                _cullCacheRect = srect;
            }
            canvas.Save();
            canvas.ClipRect(sdev);                             // device: clip before the scale
            canvas.Clear(background);
            if (_zoom != 1f) canvas.Scale(_zoom);              // …then draw in document units
            _rasterizer.Paint(canvas, _cullCache);
            canvas.Restore();
            LastFrame = new FrameTimings(0, 0, 0, Ms(f0, Stopwatch.GetTimestamp()),
                sdev.Width * sdev.Height, FastPath: true);
            return sdev;
        }

        var list = BuildFrame(width, height);
        var t0 = Stopwatch.GetTimestamp();
        var prev = _lastPresentedW == width && _lastPresentedH == height ? _lastPresented : null;
        // Document dimensions: the commands being diffed are in document space, so the bound they
        // are clamped against must be too — at zoom 2 the host's pixels are twice the page's.
        var damage = Paint.DamageDiff.Compute(prev, list.Commands, width / _zoom, height / _zoom);
        damage = SurfaceDamage(list.Commands, damage, prune: true); // frames may ALSO have arrived
        var t1 = Stopwatch.GetTimestamp();
        _lastPresented = list.Commands;
        _lastList = list;
        // Stamp HERE, not inside BuildFrame: the stamp vouches for the RETAINED list. Other
        // BuildFrame callers (headless layout, the threaded host, the a11y tree) never update
        // _lastList — a stamp there would let the fast path re-raster a stale list.
        _builtVersion = _inputsVersion;
        _lastPresentedW = width; _lastPresentedH = height;
        // Pre-extract the (few) DrawSurface commands so fast frames need not walk the whole list.
        List<DrawSurface>? surfaces = null;
        foreach (var cmd in list.Commands)
            if (cmd is DrawSurface ds) (surfaces ??= new(2)).Add(ds);
        _lastListSurfaces = surfaces?.ToArray() ?? [];
        if (damage.IsEmpty)
        {
            LastFrame = new FrameTimings(_tLayout, _tPaint, Ms(t0, t1), 0, 0);
            return null;                                       // identical frame — nothing to present
        }

        var rect = DeviceRect(damage, width, height);
        if (rect.IsEmpty)
        {
            LastFrame = new FrameTimings(_tLayout, _tPaint, Ms(t0, t1), 0, 0);
            return null;                                       // nothing of it lands on the surface
        }
        canvas.Save();
        canvas.ClipRect(rect);                                 // device: clip before the scale
        canvas.Clear(background);                              // Clear respects the clip
        if (_zoom != 1f) canvas.Scale(_zoom);                  // …then draw in document units
        _rasterizer.Paint(canvas, list);                       // full list; Skia rejects outside the clip
        canvas.Restore();
        LastFrame = new FrameTimings(_tLayout, _tPaint, Ms(t0, t1), Ms(t1, Stopwatch.GetTimestamp()),
            rect.Width * rect.Height);
        return rect;
    }

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The UI-thread half of the commit-snapshot seam: lay out, scroll the caret into view,
    /// and paint the render tree (with caret/selection/focus-ring) into an immutable
    /// <see cref="DisplayList"/> — <b>without rasterising</b>. A threaded host hands this to a render
    /// thread (see <c>ThreadedPresenter</c>); <see cref="Render"/> just rasterises it inline.</summary>
    public DisplayList BuildFrame(float hostWidth, float hostHeight)
    {
        // Sizes arrive in the WINDOW's logical pixels and the document is laid out at size/Zoom.
        // Dividing here — rather than in each caller — is what keeps Render, the semantics tree and
        // every test speaking one coordinate space.
        float width = hostWidth / _zoom, height = hostHeight / _zoom;
        var t0 = Stopwatch.GetTimestamp();
        // @media and viewport units both depend on the viewport size — re-resolve styles when
        // either axis changes (height-qualified queries are how phone landscape and a desktop
        // window are told apart; vh/vw are resolved to px at style time and must be re-folded).
        if ((_hasMedia || _hasViewportUnits)
            && (Math.Abs(width - _viewportWidth) > 0.5f || Math.Abs(height - _viewportHeight) > 0.5f))
        {
            _viewportWidth = width;
            _viewportHeight = height;
            Rebuild();
        }
        EnsureFontFaces();
        _layout.Layout(_root, width, height);
        CaptureVirtualHeights(_root); // measured pitches + scroll anchoring, before anything reads offsets
        _laidOutWidth = width; _laidOutHeight = height; _layoutDirty = false;
        ScrollCaretIntoView();  // after layout, before paint: keep the caret visible in a scrolled field
        ScrollCaretIntoViewX(); // and horizontally, in a single-line (nowrap) field
        var t1 = Stopwatch.GetTimestamp();
        _tLayout = Ms(t0, t1);

        var list = _painter.Build(_root);
        // Caret + selection are drawn outside the scrolled subtree, so clip them to the focused
        // field's scroll box — otherwise they'd draw over neighbours when the field is scrolled.
        var clip = CaretClipRect();
        if (clip is { } c) list.Add(new PushClip(c.X, c.Y, c.W, c.H, c.Radius));
        AppendSelection(list);
        AppendCompositionUnderline(list);
        AppendCaret(list);
        if (clip is not null) list.Add(new PopClip());
        AppendFocusRing(list);
        _tPaint = Ms(t1, Stopwatch.GetTimestamp());
        return list;
    }

    private const char MaskChar = '•'; // • — the bullet a masked (data-mask) field paints per character
    private const double MaskPeekSeconds = 1.4; // how long a masked field shows its last-typed char

    // Mask <paramref name="plain"/> for a data-mask field: one bullet per UTF-16 unit (so caret/
    // selection/scroll offsets line up 1:1 with the mask), except the one character being peeked
    // (_maskRevealPos) after a keystroke, which stays visible until Animate() expires the peek.
    private string MaskText(string plain)
    {
        if (!_focusMask) return plain;
        var arr = new char[plain.Length];
        for (var i = 0; i < plain.Length; i++) arr[i] = i == _maskRevealPos ? plain[i] : MaskChar;
        return new string(arr);
    }

    // The text to DISPLAY/measure for the focused field: the raw edit buffer (or the bound value),
    // masked when the field opted in via data-mask (e.g. <cupri-password>).
    private string FocusedDisplayText()
    {
        var value = _editBuffer ?? (_focusKey is null ? null : BindingEngine.Resolve(_model, _focusKey)?.ToString()) ?? "";
        return MaskText(value);
    }

    // If the caret moved (typing/nav, not wheel) and its field scrolls, nudge that container's
    // ScrollY so the caret's row sits inside the visible band.
    /// <summary>Ask for the caret to be scrolled back into view on the next frame, even though it
    /// has not moved.
    ///
    /// The caret-follow normally fires when the caret itself moves, which is the right trigger for
    /// typing. It is the wrong trigger for the VIEWPORT moving: tapping a field sets the caret while
    /// the window is still full height, and the soft keyboard only takes its half of the screen a
    /// moment later. By then the caret has not moved, so nothing looked again — and the field you
    /// had just tapped sat behind the keyboard. A host calls this whenever the usable area changes.</summary>
    public void EnsureCaretVisible()
    {
        if (_focusKey is null) return;
        _caretMoved = true;
        Refresh();
    }

    private void ScrollCaretIntoView()
    {
        if (!_caretMoved || _focusKey is null) return;
        _caretMoved = false;
        var field = FindFocused(_root);
        if (field is null) return;
        var sc = ScrollableContainer(field);
        if (sc is null) return;

        var anchor = FindCaretAnchor(field) ?? field;
        var value = FocusedDisplayText();
        var caret = Math.Clamp(_caret, 0, value.Length);
        var t = RowForCaret(BuildTextRows(anchor, value), caret); // wrap-aware for textarea + wrapped field
        float rowY = t.Y, rowH = t.Height;

        // Reveal the FIELD's border box, not just the caret's text row — with breathing room.
        // Scrolling the bare row flush to the band's last pixel is technically "visible", but on a
        // phone it parks the field's bottom chrome exactly against whatever sits below the
        // scroller (the tab bar), which reads as the keyboard/footer covering the input — the
        // device report that prompted this. A field taller than the band (a big textarea) falls
        // back to the row, which is the only part that MUST be seen.
        const float margin = 8f;
        var (_, fieldY) = PaintedTopLeft(field);
        float top = rowY, bottom = rowY + rowH;
        var boxTop = fieldY - margin;
        var boxBottom = fieldY + field.Height + margin;

        var (_, scY) = PaintedTopLeft(sc);
        var bandTop = scY + sc.ContentTopInset;
        var bandBottom = bandTop + sc.ContentBoxHeight;
        if (boxBottom - boxTop <= sc.ContentBoxHeight) { top = boxTop; bottom = boxBottom; }

        var newScroll = sc.ScrollY;
        if (top < bandTop) newScroll = sc.ScrollY + (top - bandTop);              // scroll up to reveal
        else if (bottom > bandBottom) newScroll = sc.ScrollY + (bottom - bandBottom); // down
        sc.ScrollY = Math.Clamp(newScroll, 0, sc.MaxScrollY);
    }

    // Keep the caret horizontally visible in a single-line (white-space:nowrap) field by scrolling its
    // content — mirrors ScrollCaretIntoView on the X axis. Preserved across rebuilds via NodeState.
    private void ScrollCaretIntoViewX()
    {
        if (_focusKey is null) return;
        var field = FindFocused(_root);
        if (field is null || field.Style.WhiteSpace != WhiteSpaceMode.NoWrap) return;
        if (field.Element?.HasAttribute("data-multiline") == true) return;

        var anchor = FindCaretAnchor(field) ?? field;
        var value = FocusedDisplayText();
        var caret = Math.Clamp(_caret, 0, value.Length);
        var caretX = _fonts.MeasureText(anchor.Style, value[..caret]); // from the text start
        var full = value.Length == 0 ? 0 : _fonts.MeasureText(anchor.Style, value);
        var boxW = field.ContentBoxWidth;

        var sx = field.ScrollX;
        if (caretX - sx < 0) sx = caretX;                    // caret ran off the left → reveal it
        else if (caretX - sx > boxW) sx = caretX - boxW;     // ran off the right → reveal it
        field.ScrollX = Math.Clamp(sx, 0, MathF.Max(0, full - boxW));
    }

    // The focused field's scroll-container content box (painted), for clipping caret/selection; null if none.
    private (float X, float Y, float W, float H, float Radius)? CaretClipRect()
    {
        if (_focusKey is null) return null;
        var focused = FindFocused(_root);
        // A single-line (nowrap) field scrolls horizontally under overflow:hidden — clip the caret and
        // selection to its content box so they never draw past the field edge when scrolled.
        var sc = ScrollableContainer(focused)
            ?? (focused is { } f && f.Style.WhiteSpace == WhiteSpaceMode.NoWrap ? f : null);
        if (sc is null) return null;
        var (sx, sy) = PaintedTopLeft(sc);
        return (sx + sc.ContentLeftInset, sy + sc.ContentTopInset,
                sc.Width - sc.HorizontalInsets, sc.Height - sc.VerticalInsets, 0f);
    }

    // Draw the text caret for the focused field, after the field's own text.
    private void AppendCaret(DisplayList list)
    {
        if (ComputeCaretRect() is not { } r) return;
        var field = FindFocused(_root)!;
        var anchor = FindCaretAnchor(field) ?? field;
        list.Add(new FillRect(r.X, r.Y, r.W, r.H, 0f, anchor.Style.Color));
    }

    /// <summary>The caret's painted rectangle in logical px — shared by the caret paint and the
    /// IME state (candidate windows position against this). Null when nothing is focused.</summary>
    internal (float X, float Y, float W, float H)? ComputeCaretRect()
    {
        if (_focusKey is null) return null;
        var field = FindFocused(_root);
        if (field is null) return null;

        // Anchor the caret to the node that actually paints the value text (a component may
        // nest it in a padded span — e.g. cupri-number puts its padding on the text span, not
        // the field), so the caret lines up regardless of where the padding lives.
        var anchor = FindCaretAnchor(field) ?? field;
        var value = FocusedDisplayText();
        var caret = Math.Clamp(_caret, 0, value.Length);
        var ch = anchor.Style.FontSize * 1.1f;

        // Place the caret at its real painted position via wrap-aware visual rows — correct for a
        // textarea AND for a single-line field whose long value has soft-wrapped to several rows.
        var rows = BuildTextRows(anchor, value);
        var target = RowForCaret(rows, caret);
        var col = Math.Clamp(caret - target.Start, 0, target.Text.Length);
        var cx = target.X + _fonts.MeasureText(anchor.Style, target.Text[..col]);
        var cy = target.Y + (target.Height - ch) / 2f;
        return (cx, cy, 2f, ch);
    }

    /// <summary>Underline the IME preedit (the marked range) — the visual difference between
    /// text that is committed and text the IME is still deciding about.</summary>
    private void AppendCompositionUnderline(DisplayList list)
    {
        if (_compStart < 0 || _focusKey is null) return;
        var field = FindFocused(_root);
        if (field is null) return;
        var anchor = FindCaretAnchor(field) ?? field;
        var value = FocusedDisplayText();
        var start = Math.Clamp(_compStart, 0, value.Length);
        var end = Math.Clamp(_compStart + _compLen, 0, value.Length);
        if (end <= start) return;

        var rows = BuildTextRows(anchor, value);
        foreach (var row in rows)
        {
            var s0 = Math.Max(start, row.Start);
            var s1 = Math.Min(end, row.Start + row.Text.Length);
            if (s1 <= s0) continue;
            var x0 = row.X + _fonts.MeasureText(anchor.Style, row.Text[..(s0 - row.Start)]);
            var x1 = row.X + _fonts.MeasureText(anchor.Style, row.Text[..(s1 - row.Start)]);
            var uy = row.Y + (row.Height + anchor.Style.FontSize * 1.1f) / 2f + 1f;
            list.Add(new FillRect(x0, uy, Math.Max(1f, x1 - x0), 1.5f, 0f, anchor.Style.Color));
        }
    }

    // Draw the text selection highlight (behind the text) for the focused field.
    private void AppendSelection(DisplayList list)
    {
        if (_focusKey is null || _selAnchor == _caret) return;
        var field = FindFocused(_root);
        if (field is null) return;
        var anchor = FindCaretAnchor(field) ?? field;
        var value = FocusedDisplayText();
        int s = Math.Clamp(Math.Min(_selAnchor, _caret), 0, value.Length);
        int e = Math.Clamp(Math.Max(_selAnchor, _caret), 0, value.Length);
        if (s == e) return;

        var ch = anchor.Style.FontSize * 1.1f;
        var sel = new SKColor(0x2F, 0x6F, 0xED, 0x40); // translucent selection blue

        // Per visual row (wrap-aware) highlight — for both a textarea and a soft-wrapped single-line
        // field. A selected newline at a logical line's end shows a small trailing sliver.
        foreach (var row in BuildTextRows(anchor, value))
        {
            int re = row.Start + row.Text.Length;
            if (e <= row.Start || s > re) continue;
            int ca = Math.Clamp(s - row.Start, 0, row.Text.Length);
            int cb = Math.Clamp(e - row.Start, 0, row.Text.Length);
            var x1 = row.X + _fonts.MeasureText(anchor.Style, row.Text[..ca]);
            var x2 = row.X + _fonts.MeasureText(anchor.Style, row.Text[..cb]);
            var w = (x2 - x1) + (row.NewlineAfter && e > re ? 6f : 0f); // selected trailing newline → sliver
            if (w > 0) list.Add(new FillRect(x1, row.Y + (row.Height - ch) / 2f, w, ch, 0f, sel));
        }
    }

    /// <summary>A visual (post-wrap) row of a field's text: the buffer range it covers
    /// (<c>[Start,End]</c>, contiguous so no caret position falls in a gap), its visible text, and
    /// the absolute top-left it is PAINTED at (matching the painter's <c>AbsoluteBox(textNode)+line.X/Y</c>),
    /// so caret/selection/hit-testing line up with wrapped text instead of a synthetic line grid.</summary>
    private readonly record struct TextRow(int Start, int End, string Text, float X, float Y, float Height, bool NewlineAfter);

    private static List<TextRow> BuildTextRows(RenderNode anchor, string value)
    {
        var rows = new List<TextRow>();
        var logical = value.Split('\n');
        // The textarea renders one <div class="cupri-ta-line"> per logical line, in order.
        var lineDivs = anchor.Children.Where(c => c.Element is not null).ToList();
        var lh = FontService.LineHeightPx(anchor.Style);
        var offset = 0;
        for (var i = 0; i < logical.Length; i++)
        {
            var lineText = logical[i];
            var newlineAfter = i < logical.Length - 1;
            var div = i < lineDivs.Count ? lineDivs[i] : null;
            // Textarea: one line-<div> per logical line, each wrapping a text node. Single-line field:
            // no line divs — the value text node sits directly under the anchor span. Handle both.
            var textNode = (div ?? anchor).Children.FirstOrDefault(c => c.IsText);

            if (lineText.Length > 0 && textNode?.Lines is { Count: > 0 } lines)
            {
                var (tx, ty) = PaintedTopLeft(textNode);
                // Each visual row's start column in the logical line (skips whitespace consumed at wraps).
                var cols = new int[lines.Count];
                var cursor = 0;
                for (var r = 0; r < lines.Count; r++)
                {
                    var col = lineText.IndexOf(lines[r].Text, Math.Min(cursor, lineText.Length), StringComparison.Ordinal);
                    cols[r] = col < 0 ? Math.Min(cursor, lineText.Length) : col;
                    cursor = cols[r] + lines[r].Text.Length;
                }
                for (var r = 0; r < lines.Count; r++)
                {
                    // Cover contiguously up to the next row's start (or the line's end for the last
                    // row) so a caret in consumed/trailing whitespace still maps here — no gaps, so the
                    // caret/scroll never falls through to the bottom row.
                    var end = offset + (r + 1 < lines.Count ? cols[r + 1] : lineText.Length);
                    rows.Add(new TextRow(offset + cols[r], end, lines[r].Text,
                        tx + lines[r].X, ty + lines[r].Y, lines[r].Height, r == lines.Count - 1 && newlineAfter));
                }
            }
            else
            {
                // Empty logical line (or no laid-out text): a zero-width row at the line box.
                var (dx, dy) = PaintedTopLeft(div ?? anchor);
                rows.Add(new TextRow(offset, offset, "",
                    dx + (div?.ContentLeftInset ?? 0f), dy + (div?.ContentTopInset ?? 0f), lh, newlineAfter));
            }
            offset += lineText.Length + 1;
        }
        return rows;
    }

    // The visual row a caret sits in: the row whose [Start,End] contains it, else the nearest by
    // offset (never the last row by default, which would jump the caret/scroll to the bottom).
    private static TextRow RowForCaret(List<TextRow> rows, int caret)
    {
        var best = rows[0];
        var bestDist = int.MaxValue;
        foreach (var r in rows)
        {
            var d = caret < r.Start ? r.Start - caret : caret > r.End ? caret - r.End : 0;
            if (d <= bestDist) { bestDist = d; best = r; } // <= keeps the later row on a boundary tie
        }
        return best;
    }

    // The caret index in the focused field's buffer nearest the point (x,y) — click-to-place-caret.
    private int CaretFromPoint(RenderNode field, RenderNode anchorNode, float x, float y)
    {
        var value = _editBuffer ?? "";
        if (value.Length == 0) return 0;

        // Pick the visual (wrap-aware) row nearest y, then the nearest column within it. Works for
        // both a textarea and a single-line field whose long value soft-wraps to several rows.
        _ = field;
        var rows = BuildTextRows(anchorNode, value);
        var row = rows[0];
        var bestDy = float.MaxValue;
        foreach (var r in rows)
        {
            var dy = y < r.Y ? r.Y - y : y > r.Y + r.Height ? y - (r.Y + r.Height) : 0f;
            if (dy < bestDy) { bestDy = dy; row = r; }
        }
        return row.Start + NearestColumn(anchorNode.Style, row.Text, x - row.X);
    }

    // Painted top-left of a node: like HitTesting.AbsoluteBox but subtracts each scrollable ancestor's
    // clamped scroll offset, so caret/selection line up with where the Painter actually draws the text
    // (the Painter shifts a scrollable node's children up by its ScrollY).
    private static (float X, float Y) PaintedTopLeft(RenderNode node)
    {
        float x = 0, y = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            x += n.X; y += n.Y;
            if (n.Parent is { } p)
            {
                if (p.IsScrollable) y -= Math.Clamp(p.ScrollY, 0, p.MaxScrollY);
                x -= p.ScrollX; // horizontal caret-follow shift (0 unless a single-line field)
            }
            if (n.IsTopLayer) break;
        }
        return (x, y);
    }

    // The nearest scrollable ancestor of a node (or the node itself), for clipping/scrolling; null if none.
    private static RenderNode? ScrollableContainer(RenderNode? node)
    {
        for (var n = node; n is not null; n = n.Parent) if (n.IsScrollable) return n;
        return null;
    }

    // The scrollbar thumb rect (painted), mirroring the Painter; null if not scrollable.
    private (float X, float Y, float W, float H)? ThumbRect(RenderNode n)
    {
        if (!n.IsScrollable) return null;
        var (ax, ay) = PaintedTopLeft(n);
        var boxH = n.ContentBoxHeight;
        var thumbH = MathF.Max(28f, boxH * boxH / n.ScrollContentHeight);
        var thumbY = ay + n.ContentTopInset + Math.Clamp(n.ScrollY, 0, n.MaxScrollY) / n.MaxScrollY * (boxH - thumbH);
        var thumbX = ax + n.Width - n.BorderRightW - 8f;
        return (thumbX, thumbY, 5f, thumbH);
    }

    // Is (x,y) over the resize grip (bottom-right corner) of a resizable node?
    private bool InResizeGrip(RenderNode n, float x, float y)
    {
        if (n.Style.Resize == ResizeMode.None) return false;
        var (ax, ay) = PaintedTopLeft(n);
        float right = ax + n.Width, bottom = ay + n.Height;
        const float hot = 18f;
        return x >= right - hot && x <= right + 2 && y >= bottom - hot && y <= bottom + 2;
    }

    // Clamp a dragged border-box size to a floor + the element's min/max-* (resolved approximately).
    private float ClampResize(RenderNode n, float value, bool horizontal)
    {
        var s = n.Style;
        float min = 24f, max = float.MaxValue;
        if (horizontal)
        {
            if (s.MinWidth.IsDefinite) min = MathF.Max(min, s.MinWidth.Resolve(_viewportWidth) + n.HorizontalInsets);
            if (s.MaxWidth.IsDefinite) max = s.MaxWidth.Resolve(_viewportWidth) + n.HorizontalInsets;
        }
        else
        {
            if (s.MinHeight.IsDefinite) min = MathF.Max(min, s.MinHeight.Resolve(_viewportWidth) + n.VerticalInsets);
            if (s.MaxHeight.IsDefinite) max = s.MaxHeight.Resolve(_viewportWidth) + n.VerticalInsets;
        }
        return Math.Clamp(value, min, MathF.Max(min, max));
    }

    // The column in <paramref name="text"/> whose x-offset is nearest <paramref name="localX"/>.
    private int NearestColumn(ComputedStyle style, string text, float localX)
    {
        var bestCol = 0;
        var bestDist = MathF.Abs(localX);
        for (var i = 1; i <= text.Length; i++)
        {
            var d = MathF.Abs(localX - _fonts.MeasureText(style, text[..i]));
            if (d < bestDist) { bestDist = d; bestCol = i; }
        }
        return bestCol;
    }

    // Draw a keyboard focus ring around the focused control (only after Tab — "focus-visible").
    private void AppendFocusRing(DisplayList list)
    {
        if (!_focusVisible || _kbIndex < 0) return;
        var f = Focusables();
        if (_kbIndex >= f.Count) return;
        var focused = f[_kbIndex];
        var (x, y, w, h) = HitTesting.ScreenBox(focused);
        var clips = 0;
        // Focus is painted after the main display list. Reapply ancestor clipping,
        // stopping at top-layer boundaries just as the ordinary paint path does.
        for (var child = focused; !child.IsTopLayer && child.Parent is { } parent; child = parent)
        {
            if (parent.Style.Overflow == OverflowMode.Visible) continue;
            var box = HitTesting.ScreenBox(parent);
            list.Add(new PushClip(box.X + parent.BorderLeftW, box.Y + parent.BorderTopW,
                MathF.Max(0, box.W - parent.BorderLeftW - parent.BorderRightW),
                MathF.Max(0, box.H - parent.BorderTopW - parent.BorderBottomW), parent.Style.BorderRadius));
            clips++;
        }
        const float t = 2f, pad = 2f;                 // ring thickness + gap outside the border box
        var (rx, ry, rw, rh) = (x - pad, y - pad, w + 2 * pad, h + 2 * pad);
        var c = new SKColor(0x2F, 0x6F, 0xED);        // accessible focus blue
        list.Add(new FillRect(rx, ry, rw, t, 0f, c));           // top
        list.Add(new FillRect(rx, ry + rh - t, rw, t, 0f, c));  // bottom
        list.Add(new FillRect(rx, ry, t, rh, 0f, c));           // left
        list.Add(new FillRect(rx + rw - t, ry, t, rh, 0f, c));  // right
        while (clips-- > 0) list.Add(new PopClip());
    }

    private static RenderNode? FindFocused(RenderNode n)
    {
        if (n.Element?.HasAttribute("data-focus") == true) return n;
        foreach (var c in n.Children) { var f = FindFocused(c); if (f is not null) return f; }
        return null;
    }

    // The descendant a component marks as the value-text node (data-caret-anchor); null → use the field itself.
    private static RenderNode? FindCaretAnchor(RenderNode n)
    {
        if (n.Element?.HasAttribute("data-caret-anchor") == true) return n;
        foreach (var c in n.Children) { var f = FindCaretAnchor(c); if (f is not null) return f; }
        return null;
    }

    /// <summary>Build the committed display-list snapshot without rasterising (the seam).</summary>
    public DisplayList BuildDisplayList(float width, float height)
    {
        EnsureFontFaces();
        _layout.Layout(_root, width, height);
        return _painter.Build(_root);
    }

    // ---- interaction (Layer 0 → hit-test → dispatch) -------------------------
    // Registered selector → handler. The selector is COMPILED once here (see Matches) rather than
    // re-parsed on every click, hover and cursor query.
    private readonly List<(string Selector, AngleSharp.Css.Dom.ISelector? Compiled, Action<CupriPointerEvent> Handler)> _clickHandlers = new();

    /// <summary>Register a click handler matched by CSS selector (bubbles from target up).</summary>
    /// <remarks>The built-in interaction primitives claim a click FIRST and stop the walk, so a selector
    /// matching one of them never runs. The common surprise is <c>OnClick("a", ...)</c>: it is silent for
    /// EVERY anchor, because the link branch has already raised <see cref="Navigated"/> and returned.
    /// Route links off <see cref="Navigated"/> instead — it carries every non-<c>#</c> href, with
    /// <c>External</c> separating an in-app path from one a host should open externally; a host's own
    /// re-emission of it (<c>IWebBridge.Navigate</c>, <c>DesktopHost.OpenExternal</c>) is the external
    /// subset only, so watching that instead makes relative and custom-scheme links look dropped when they
    /// are not. Steppers, video transport, window commands, toggles and the ARIA control roles shadow a
    /// selector the same way. A shadowed handler looks exactly like one whose selector does not match.</remarks>
    public CupriDocument OnClick(string selector, Action<CupriPointerEvent> handler)
    {
        _clickHandlers.Add((selector, CompileSelector(selector), handler));
        return this;
    }

    private readonly Dictionary<string, Action> _shortcuts = new();

    /// <summary>Register a keyboard shortcut. <paramref name="mods"/> is usually <see cref="KeyMods.Ctrl"/>
    /// (Cmd maps to it on macOS); <paramref name="key"/> is either a single character, e.g. <c>"k"</c>, or
    /// the name of one of the editing keys — <c>"Enter"</c>, <c>"Escape"</c>, <c>"Tab"</c>, <c>"Space"</c>,
    /// <c>"Backspace"</c>, <c>"Delete"</c>, <c>"Home"</c>, <c>"End"</c> or an arrow (case-insensitive).
    /// A Ctrl shortcut fires anywhere (even while editing a field); a plain-key one only fires when no
    /// field is focused, so a bare <c>"Enter"</c> binding cannot eat a newline being typed. Escape is the
    /// exception: it fires below the engine's own dismissals (a menu, an open overlay and video fullscreen
    /// each win) but above the plain blur, so it still means "cancel" while a field has focus.
    /// The host must deliver the chord — the built-in Web/Viewer hosts forward Ctrl/Cmd + letter.
    ///
    /// <para>A modifier plus <c>"Tab"</c> is delivered on desktop and Android but, in practice, never in
    /// a browser: Ctrl+Tab and Ctrl+Shift+Tab switch browser tabs and Alt+Tab belongs to the window
    /// manager, so the keydown does not reach the page at all and <c>preventDefault</c> cannot claim it.
    /// The hosts forward the modifiers correctly (#96) — the platform is what withholds the chord, which
    /// is why this is documented rather than refused: the same registration works elsewhere.</para></summary>
    /// <exception cref="ArgumentException">The key can never be delivered, so the binding would be dead:
    /// anything that is neither a single character nor one of the names above. Two shipped features once
    /// did nothing for weeks behind exactly that silence (#88), so it fails at the call site instead.</exception>
    public CupriDocument OnShortcut(KeyMods mods, string key, Action handler)
    {
        if (!IsBindable(key))
            throw new ArgumentException(
                $"\"{key}\" is not a bindable shortcut key — it can never be delivered, so the handler " +
                $"would never run. Use a single character (\"k\") or one of: {NamedKeyList}.", nameof(key));
        _shortcuts[ShortcutKey(mods, key)] = handler;
        return this;
    }
    private static string ShortcutKey(KeyMods mods, string key) => (mods.HasFlag(KeyMods.Ctrl) ? "ctrl+" : "") + key.ToLowerInvariant();

    /// <summary>
    /// Move keyboard focus to a bound field, as a click would. Returns false if the selector matches
    /// nothing or the match is not a bound field.
    ///
    /// <para>Exists because an app had no way to do this at all: the only focus-shaped API was
    /// <see cref="AccessibilityFocus"/>, which is keyed by accessibility path and meant for a screen
    /// reader rather than for ordinary behaviour. Without it, any "put this into the input for the
    /// user to edit" flow dead-ends — a right-click "Edit" that loads a message into a composer left
    /// the text sitting there while typing went nowhere, because choosing the menu item had moved
    /// focus off the field (#111).</para>
    ///
    /// <para>The caret lands at the end of the current value, which is what "carry on typing" wants.
    /// Use <see cref="SetFieldValue"/> to place text there first.</para>
    /// </summary>
    public bool Focus(string selector) => Bump(FocusCore(selector));

    private bool FocusCore(string selector)
    {
        EnsureLaidOut();
        if (_dom?.QuerySelector(selector) is not { } el) return false;
        if (el.GetAttribute("data-bind-value") is not { Length: > 0 } bind) return false;
        UpdateFocus(el);                       // no-ops if it is already the focused field
        var value = _model is null ? "" : BindingEngine.Resolve(_model, bind)?.ToString() ?? "";
        _editBuffer = null;
        _caret = _selAnchor = value.Length;
        _caretMoved = true;
        return true;
    }

    /// <summary>Remove keyboard focus from whatever holds it, committing a field's pending edit the
    /// way clicking away would.</summary>
    public bool Blur() => Bump(_focusKey is not null && UpdateFocus(null));

    /// <summary>
    /// Set a bound field's text from the app — including while that field has focus, which is the
    /// whole point.
    ///
    /// <para>Assigning the bound property directly does not work on a focused field: the component
    /// edits a permissive buffer that is only committed on blur, so the assignment is discarded and
    /// the next keystroke lands at the old caret offset inside the old text. Completing "@da" to
    /// "@dagger" and then typing produced "@dalook at thisgger" — the completion vanished and the
    /// sentence was interleaved into the fragment (#111). This writes the model AND resets the
    /// buffer and caret together, so the field agrees with the model immediately.</para>
    ///
    /// <para>Focus is preserved, so the user carries on typing where they left off. The caret is
    /// placed at the end of the new text.</para>
    /// </summary>
    public bool SetFieldValue(string selector, string text) => Bump(SetFieldValueCore(selector, text));

    private bool SetFieldValueCore(string selector, string text)
    {
        EnsureLaidOut();
        if (_dom?.QuerySelector(selector) is not { } el) return false;
        if (el.GetAttribute("data-bind-value") is not { Length: > 0 } bind) return false;
        if (_model is null || !BindingEngine.TrySet(_model, bind, text)) return false;
        // The buffer outranks the model for the FOCUSED field, so leaving it stale is exactly the
        // bug this method exists to fix — clearing it makes the next read come from the model.
        if (bind == _focusKey)
        {
            _editBuffer = null;
            _caret = _selAnchor = text.Length;
            _caretMoved = true;
        }
        Refresh();
        return true;
    }

    /// <summary>
    /// May a BARE Up/Down reach an app's shortcut even though a field has focus?
    ///
    /// <para>Up and Down are the only editing keys a focused text field does not act on — there is no
    /// vertical caret movement in a textarea today — so they are the only ones that can be offered to
    /// an app without taking something away. Everything else is spoken for: Left/Right/Home/End move
    /// the caret, Backspace/Delete edit, Enter submits, Space types, Tab traverses, Escape blurs. That
    /// is why this is a two-key exception and not the general "any bare key falls through" rule: a
    /// bare-letter binding would silently eat typing, which is what the original no-bare-keys-while-
    /// focused rule existed to prevent (#111).</para>
    ///
    /// <para>An arrow over an OPEN LIST is the one moment an app most needs a bare key, and it is
    /// exactly the moment a field always has focus — so a typeahead was unbuildable without this.
    /// A focused <c>data-listbox</c> still wins, because the engine's own combobox is actively using
    /// those keys; an app's binding only gets what nothing else is using.</para>
    ///
    /// <para>If vertical caret movement is ever added to multiline fields, this must shrink to nothing
    /// and apps must be given a different seam — the premise here is that these two keys are inert.</para>
    /// </summary>
    private bool ArrowFreeWhileEditing(EditKey key) =>
        key is EditKey.Up or EditKey.Down && FocusedListbox() is not { Count: > 0 };

    // The editing keys an app may bind by name, and the name each binds under. Keys that cannot
    // meaningfully be bound are absent: None is "no key", ShiftTab is Tab plus a modifier, and SelectAll
    // is already Ctrl+A. A key here is looked up on the way in exactly as a character is.
    private static readonly (EditKey Key, string Name)[] NamedKeys =
    [
        (EditKey.Enter, "enter"), (EditKey.Escape, "escape"), (EditKey.Tab, "tab"), (EditKey.Space, "space"),
        (EditKey.Backspace, "backspace"), (EditKey.Delete, "delete"), (EditKey.Home, "home"), (EditKey.End, "end"),
        (EditKey.Left, "left"), (EditKey.Right, "right"), (EditKey.Up, "up"), (EditKey.Down, "down"),
    ];
    private static readonly string NamedKeyList = string.Join(", ", Array.ConvertAll(NamedKeys, k => k.Name));

    private static string? NameOf(EditKey key)
    {
        foreach (var (k, name) in NamedKeys) if (k == key) return name;
        return null;
    }

    private static bool IsBindable(string key)
    {
        if (key.Length == 1) return true;
        foreach (var (_, name) in NamedKeys)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Custom interaction primitives (extensibility): a data-* attribute → behaviour, alongside the
    // engine's built-in vocabulary (data-set-path / data-cupri-toggle / …). Fires on click AND on
    // keyboard activation (Enter/Space) since both route through ActivateFrom.
    private readonly List<(string Attr, Func<Interaction.CupriActionEvent, bool> Handler)> _actionHandlers = new();

    /// <summary>Register a custom activation primitive: when a clicked/activated element (or an
    /// ancestor) carries <paramref name="dataAttribute"/> (e.g. <c>"data-sort-by"</c>), the handler
    /// runs with the element, its attribute value and the model. Return true if it handled the event
    /// (stops bubbling + triggers a refresh). Lets components define new interactions without an engine
    /// change — the registration point the built-in <c>data-*</c> hooks were missing for third parties.</summary>
    public CupriDocument OnAction(string dataAttribute, Func<Interaction.CupriActionEvent, bool> handler)
    {
        _actionHandlers.Add((dataAttribute, handler));
        return this;
    }

    private readonly List<(string Attr, Func<Interaction.CupriActionEvent, bool> Handler)> _contextHandlers = new();

    /// <summary>The <see cref="OnAction"/> shape, for the moment a context menu OPENS rather than the
    /// moment something is clicked: when the pointer that opened it was over an element (or an
    /// ancestor) carrying <paramref name="dataAttribute"/>, the handler runs with that element, its
    /// attribute value and the bound model — so a menu item chosen afterwards can act on the row that
    /// was actually right-clicked.
    ///
    /// <para>Without this an app could not aim a context menu at all. A right-click is not a click, so
    /// <see cref="OnAction"/> never fires for it; <c>ContextRequested</c> carries a command and no
    /// target; and the pointer position that would make <see cref="HitTest"/> usable was never
    /// published. The only workable pattern left was to select on left-click and act on the selection,
    /// which is a click more than anyone expects (#85).</para>
    ///
    /// <para>Fires for a right-click and for the touch recognizer's long-press alike — both arrive
    /// through <see cref="DispatchContextMenu"/>, which is where this is raised. Returning true stops
    /// it bubbling to ancestors; the menu opens either way, because whether a handler claimed the
    /// target says nothing about whether a menu should appear.</para>
    ///
    /// <para>NAME THE ROW IN THE ATTRIBUTE — <c>data-msg="{{Id}}"</c> — and read it from
    /// <c>e.Value</c>. <c>e.Model</c> is the ROOT model, not the row's, exactly as it is for
    /// <see cref="OnAction"/>: <c>data-repeat</c> substitutes each item's bindings and then discards
    /// the item, so a <c>RenderNode</c> never learns which one produced it. The attribute value is
    /// how a repeated row is identified, and it is enough.</para></summary>
    public CupriDocument OnContext(string dataAttribute, Func<Interaction.CupriActionEvent, bool> handler)
    {
        _contextHandlers.Add((dataAttribute, handler));
        return this;
    }

    /// <summary>Where the last context menu was opened, in document coordinates, and what was under
    /// it. Null until one has opened. For apps that would rather <see cref="HitTest"/> themselves than
    /// register a handler — and for anything that needs the point rather than an element.</summary>
    public (float X, float Y, RenderNode? Target)? LastContext { get; private set; }

    private readonly List<(string Attr, Func<Interaction.CupriActionEvent, bool> Handler)> _submitHandlers = new();

    /// <summary>The <see cref="OnAction"/> shape again, for a field being SUBMITTED: when a field takes
    /// a plain Enter, the handler runs with the field — or an ancestor — carrying
    /// <paramref name="dataAttribute"/>, its attribute value and the bound model. The edit buffer is
    /// committed to the model FIRST, so a handler reading the bound value sees what was just typed.
    ///
    /// <para>A SINGLE-LINE field submits without opting in, as <c>&lt;input&gt;</c> does inside a
    /// <c>&lt;form&gt;</c> — Enter had no other meaning there worth keeping. A TEXTAREA must be marked
    /// <c>submit-on-enter</c> (engine attribute <c>data-submit-on-enter</c>), because Enter already
    /// means newline in one.</para>
    ///
    /// <para>There is no <c>&lt;form&gt;</c> element in this engine, so the ancestor carrying
    /// <paramref name="dataAttribute"/> IS the form boundary: a field with no such ancestor claims
    /// nothing and keeps the behaviour it had. That is what makes the implicit case safe — it can only
    /// reach fields an app has already scoped for submission.</para>
    ///
    /// <para>This is the chat-composer idiom — Enter sends, Shift+Enter starts a new line — and it is
    /// opt-in per field precisely because the alternatives are not. A bare <c>Enter</c>
    /// <see cref="OnShortcut"/> would eat newlines in every textarea on the page rather than the one
    /// that wants it; <c>FormSubmitted</c> is parameterless and the engine never raises it (only
    /// <see cref="SubmitForm"/> does, for prompting a password manager); and watching the bound value
    /// cannot work, because Enter and Shift+Enter both arrive as an inserted "\n" and are
    /// indistinguishable by the time the model changes (#90).</para>
    ///
    /// <para>Shift+Enter always inserts, whatever is registered. If NO handler claims the submit, the
    /// key falls through to its ordinary behaviour — a newline in a textarea, commit-and-blur in a
    /// single-line field — so a field marked <c>submit-on-enter</c> with nothing listening behaves
    /// exactly as it did without the attribute rather than swallowing Enter into silence.</para>
    ///
    /// <para>NAME THE ROW IN THE ATTRIBUTE and read it from <c>e.Value</c>, as with
    /// <see cref="OnAction"/> and <see cref="OnContext"/>: <c>e.Model</c> is the root model, not a
    /// repeated row's. <c>e.X</c>/<c>e.Y</c> are 0 — a submit comes from the keyboard and has no
    /// pointer position.</para></summary>
    public CupriDocument OnSubmit(string dataAttribute, Func<Interaction.CupriActionEvent, bool> handler)
    {
        _submitHandlers.Add((dataAttribute, handler));
        return this;
    }

    /// <summary>Offer the focused field's submit to any handler registered for an attribute the field
    /// or an ancestor carries. False if nothing claimed it — the caller then lets Enter do its ordinary
    /// work, so the attribute never eats a keystroke that goes nowhere.</summary>
    private bool RaiseSubmit()
    {
        if (_submitHandlers.Count == 0 || _focusKey is null) return false;
        for (var node = FocusedRenderNode(); node is not null; node = node.Parent)
        {
            if (node.Element is not { } el) continue;
            foreach (var (attr, handler) in _submitHandlers)
                if (el.GetAttribute(attr) is { } av
                    && handler(new Interaction.CupriActionEvent(node, el, av, _model, 0, 0)))
                    return true;
        }
        return false;
    }

    /// <summary>The render node of the focused field, found the way focus itself identifies it
    /// (<c>data-bind-value</c>, else <c>id</c>). Null when nothing is focused or the tree no longer
    /// holds it.</summary>
    private RenderNode? FocusedRenderNode()
    {
        if (_focusKey is null) return null;
        RenderNode? found = null;
        void Walk(RenderNode n)
        {
            if (found is not null) return;
            if (n.Element is { } e
                && (e.GetAttribute("data-bind-value") ?? e.GetAttribute("id")) == _focusKey) { found = n; return; }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        return found;
    }

    /// <summary>Publish the target of a context menu about to open, and offer it to any handler
    /// registered for an attribute the element or an ancestor carries.</summary>
    private void RaiseContextTarget(RenderNode? hit, float x, float y)
    {
        LastContext = (x, y, hit);
        if (_contextHandlers.Count == 0) return;
        for (var node = hit; node is not null; node = node.Parent)
        {
            if (node.Element is not { } el) continue;
            foreach (var (attr, handler) in _contextHandlers)
                if (el.GetAttribute(attr) is { } av
                    && handler(new Interaction.CupriActionEvent(node, el, av, _model, x, y)))
                    return;
        }
    }

    public RenderNode? HitTest(float x, float y) { EnsureLaidOut(); return HitTesting.HitTest(_root, Zc(x), Zc(y)); }

    /// <summary>Lay out the current tree if it hasn't been, at the last size a frame used. A rebuild or
    /// restyle throws away the laid-out tree, and hosts render once per frame and dispatch input in
    /// between — so input arriving after a hover/model change but before the next frame would hit-test a
    /// tree whose boxes are all zero, and be silently swallowed (a click landing in the same frame as the
    /// hover that preceded it did nothing). Every hit-testing entry point calls this first. It's a no-op
    /// on the common path (the frame just laid out), and the work it does is what the next frame would
    /// have done anyway.</summary>
    private void EnsureLaidOut()
    {
        if (!_layoutDirty || _laidOutWidth <= 0 || _laidOutHeight <= 0) return;
        EnsureFontFaces();
        _layout.Layout(_root, _laidOutWidth, _laidOutHeight);
        _layoutDirty = false;
    }

    /// <summary>The cursor the host should show at (x, y). A drag in progress dictates it; otherwise the
    /// drag affordance under the pointer (a resize grip or a resizable table's column edge) wins, then the
    /// nearest explicit CSS <c>cursor</c>, then one inferred from the element (pointer over links / buttons /
    /// clickables, text over text fields). Hosts call this after each move and map it to a platform cursor.</summary>
    public Style.CursorType CursorAt(float x, float y)
    {
        EnsureLaidOut();
        // 1) An active drag owns the cursor regardless of what's under the pointer — the pointer routinely
        //    leaves the grabbed control mid-drag, and flickering to whatever it passes over reads as a
        //    broken drag. Every drag the document can be in is listed here.
        if (_colPath is not null) return Style.CursorType.EwResize;
        if (_splitA is not null) return _splitVertical ? Style.CursorType.NsResize : Style.CursorType.EwResize;
        if (_reorderItems is not null) return Style.CursorType.Grabbing;
        if (_scrollDrag is not null) return Style.CursorType.Default;  // scrollbar thumb
        if (_windowDrag) return Style.CursorType.Grabbing;             // moving the window
        if (_dragging) return Style.CursorType.Pointer;                // slider thumb
        if (_textDrag) return Style.CursorType.Text;                   // drag-selecting in a field
        if (_resizeDrag is { } rd) return rd.Style.Resize switch
        {
            Style.ResizeMode.Horizontal => Style.CursorType.EwResize,
            Style.ResizeMode.Vertical => Style.CursorType.NsResize,
            _ => Style.CursorType.NwseResize,
        };

        // Reuse the hit the move dispatch just recorded for these exact coordinates on this same tree —
        // hosts call CursorAt right after DispatchPointerMove, so this usually saves the second tree walk.
        // (A restyle/rebuild replaces the tree, which invalidates the cache by identity; EnsureLaidOut
        // above guarantees the fresh tree has geometry, so re-hit-testing it is correct.)
        var hit = ReferenceEquals(_lastHitRoot, _root) && _lastHitX == x && _lastHitY == y
            ? _lastHit
            : HitTesting.HitTest(_root, x, y);
        if (hit is null) return Style.CursorType.Default;

        // 2) Drag affordances under the pointer (a corner grip / a column boundary) — before CSS, so they
        //    beat an inherited cursor the way the grab itself takes priority over a normal click.
        for (var n = hit; n is not null; n = n.Parent)
        {
            if (InResizeGrip(n, x, y)) return n.Style.Resize switch
            {
                Style.ResizeMode.Horizontal => Style.CursorType.EwResize,
                Style.ResizeMode.Vertical => Style.CursorType.NsResize,
                _ => Style.CursorType.NwseResize,
            };
            if (ColumnBoundaryAt(n, x) is not null) return Style.CursorType.EwResize;
            // A window-drag handle is a grab affordance, not a click one — and it belongs in this
            // section rather than with the inferred pointers below, so a title bar containing a
            // button still reads as grabbable everywhere the button is not.
            if (n.Element?.HasAttribute("data-window-drag") == true && WindowMoveRequested is not null)
                return Style.CursorType.Grab;
        }

        // 3) The nearest explicit CSS cursor (Auto = unspecified → keep looking up the chain).
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Style.Cursor != Style.CursorType.Auto) return n.Style.Cursor;

        // 4) Inferred: disabled controls → not-allowed; text fields → text; anything that acts on a
        //    click → pointer. Checked innermost-out, so a disabled control inside a clickable row wins.
        for (var n = hit; n is not null; n = n.Parent)
        {
            if (n.Element is not { } el) continue;
            if (IsDisabled(el)) return Style.CursorType.NotAllowed;
            if (el.GetAttribute("role") is "textbox" or "spinbutton") return Style.CursorType.Text;
            if (ActsOnClick(el)) return Style.CursorType.Pointer;
        }

        // 5) A checkbox/radio/switch's text label activates the control (HTML <label> behaviour), so it
        //    gets the control's cursor — same walk the click uses, so the two always agree.
        return LabelTargets(hit).Any() ? Style.CursorType.Pointer : Style.CursorType.Default;
    }

    /// <summary>Would a click on this element (not its ancestors) do something? The single definition
    /// behind the pointer cursor: the interactive roles, links, every built-in <c>data-*</c> activation
    /// hook, and — the ones easily forgotten — app-registered <see cref="OnClick"/> selectors and
    /// <see cref="OnAction"/> attributes, which <see cref="ActivateFrom"/> honours and so must show a
    /// pointer too (a sidebar nav row wired only by <c>OnClick</c> is the canonical case).</summary>
    private bool ActsOnClick(IElement el) =>
        el.GetAttribute("role") is "link" or "button" or "switch" or "checkbox" or "radio" or "slider"
        || el.LocalName is "a" or "button"
        || el.HasAttribute("data-set-path") || el.HasAttribute("data-set-toggle")
        || el.HasAttribute("data-cupri-toggle") || el.HasAttribute("data-cupri-dismiss")
        || el.HasAttribute("data-cupri-step")
        || _actionHandlers.Exists(h => el.HasAttribute(h.Attr))
        || _clickHandlers.Exists(h => Matches(el, h.Compiled));

    /// <summary>A control the author marked unavailable (<c>aria-disabled</c>, or the <c>disabled</c>
    /// class/attribute the components use, e.g. a pagination arrow on the first page). It keeps its role
    /// for a11y but drops its activation hooks, so the cursor must not promise a click. The definition
    /// lives on <see cref="Accessibility.AccessibilityTree"/> so the semantics tree and the cursor can
    /// never disagree about what counts as disabled.</summary>
    private static bool IsDisabled(IElement el) => Accessibility.AccessibilityTree.IsDisabled(el);

    /// <summary>The CSS <c>cursor</c> keyword for a <see cref="Style.CursorType"/> — for web hosts that set
    /// <c>canvas.style.cursor</c> from <see cref="CursorAt"/>.</summary>
    public static string CursorCss(Style.CursorType c) => c switch
    {
        Style.CursorType.Pointer => "pointer",
        Style.CursorType.Text => "text",
        Style.CursorType.Wait => "wait",
        Style.CursorType.Progress => "progress",
        Style.CursorType.Help => "help",
        Style.CursorType.Crosshair => "crosshair",
        Style.CursorType.Move => "move",
        Style.CursorType.NotAllowed => "not-allowed",
        Style.CursorType.Grab => "grab",
        Style.CursorType.Grabbing => "grabbing",
        Style.CursorType.EwResize => "ew-resize",
        Style.CursorType.NsResize => "ns-resize",
        Style.CursorType.NeswResize => "nesw-resize",
        Style.CursorType.NwseResize => "nwse-resize",
        Style.CursorType.None => "none",
        _ => "default",
    };

    /// <summary>Build the platform-neutral semantics tree (§5) at the given size. Uses the SAME
    /// focusable predicate as Tab order and marks the currently focused control, so what a screen
    /// reader is told never diverges from what the keyboard does. Skips re-layout when the frame
    /// just laid out at this size (the common per-frame case).</summary>
    public Accessibility.AccessibilityNode BuildAccessibilityTree(float width, float height)
    {
        // The caller passes the WINDOW's size; the document is laid out at size/Zoom like every
        // other path.
        float w = width / _zoom, h = height / _zoom;   // same division BuildFrame performs
        if (_layoutDirty || _laidOutWidth != w || _laidOutHeight != h)
        {
            EnsureFontFaces();
            _layout.Layout(_root, w, h);
            _laidOutWidth = w;
            _laidOutHeight = h;
            _layoutDirty = false;
        }
        var focused = FindFocused(_root) ?? CurrentFocusNode();
        var tree = Accessibility.AccessibilityTree.Build(_root, IsFocusable, focused);

        // Bounds go back out in the HOST's space. A screen reader draws its focus rectangle from
        // these and a magnifier follows them, so reporting document coordinates on a zoomed page
        // would point assistive technology at the wrong place on screen — the one bug that would
        // make zoom actively hostile to the users it exists for.
        if (_zoom != 1f) ScaleBounds(tree, _zoom);
        return tree;
    }

    private static void ScaleBounds(Accessibility.AccessibilityNode n, float k)
    {
        var (x, y, w, h) = n.Bounds;
        n.Bounds = (x * k, y * k, w * k, h * k);
        foreach (var c in n.Children) ScaleBounds(c, k);
    }

    /// <summary>Resolve a structural path (<see cref="Accessibility.AccessibilityNode.Path"/>) back to
    /// a node in the CURRENT render tree — the identity that survives rebuilds (the same scheme scroll
    /// restoration uses). Null when the path no longer exists.</summary>
    public RenderNode? NodeAtPath(string path)
    {
        var n = _root;
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(seg, out var i) || i < 0 || i >= n.Children.Count) return null;
            n = n.Children[i];
        }
        return n;
    }

    // ---- Accessibility actions (AT bridges call these; UIA Invoke/Toggle/SetValue etc.) ----------
    // Each one resolves the path against the current tree and then reuses the ordinary interaction
    // machinery, so an AT-initiated action behaves exactly like the user doing it.

    /// <summary>Activate a node the way a click at its centre would (UIA Invoke / Toggle / Select /
    /// ExpandCollapse all funnel here). Same dispatch path as a real click, so activation rules,
    /// overlays, focus sync and disabled handling all apply. Returns true if anything changed.</summary>
    public bool AccessibilityActivate(string path) => Bump(AccessibilityActivateCore(path));
    private bool AccessibilityActivateCore(string path)
    {
        EnsureLaidOut();
        if (NodeAtPath(path) is not { } n) return false;
        if (n.Element is { } el && IsDisabled(el)) return false;
        RevealControl(path);
        EnsureLaidOut();
        if (NodeAtPath(path) is not { } target) return false;
        var (x, y) = HitTesting.ActivationPoint(target);
        // An inaccessible or occluded target must never click an unrelated control.
        for (var hit = HitTesting.HitTest(_root, x, y); hit is not null; hit = hit.Parent)
            if (ReferenceEquals(hit, target)) return DispatchClickCore(x, y, 1);
        return false;

    }

    /// <summary>Move keyboard focus to the control at (or containing) the node — UIA SetFocus.</summary>
    public bool AccessibilityFocus(string path) => Bump(AccessibilityFocusCore(path));
    private bool AccessibilityFocusCore(string path)
    {
        EnsureLaidOut();
        if (NodeAtPath(path) is not { } n) return false;
        var i = IndexOfFocusable(n);
        if (i < 0) return false;
        _kbIndex = i;
        _focusVisible = true;
        var el = Focusables()[i].Element;
        UpdateFocus(el?.GetAttribute("role") is "textbox" or "spinbutton" ? el : null);
        Refresh();
        RevealKeyboardFocus();
        return true;
    }

    /// <summary>Write text into the field at <paramref name="path"/> — what an autofill service
    /// does when it fills a username and password. Goes through the SAME binding a keystroke would,
    /// so validation, formatting and change notification all behave as though a person typed it.
    /// Returns false when the path is not a bound field.</summary>
    public bool AccessibilitySetText(string path, string text) => Bump(AccessibilitySetTextCore(path, text));

    private bool AccessibilitySetTextCore(string path, string text)
    {
        EnsureLaidOut();
        if (NodeAtPath(path)?.Element is not { } el) return false;
        if (IsDisabled(el) || el.HasAttribute("readonly") || el.GetAttribute("aria-readonly") == "true") return false;
        if (el.GetAttribute("data-bind-value") is not { Length: > 0 } bind) return false;
        if (_model is null) return false;
        if (!BindingEngine.TrySet(_model, bind, text)) return false;
        if (_focusKey == bind)
        {
            // Autofill replaces the active editor too. Otherwise its old buffer
            // paints over the new model value and commits that old value on blur.
            _editBuffer = text;
            _caret = _selAnchor = text.Length;
            _caretMoved = true;
            _compStart = -1; _compLen = 0; _compUndo = null;
            _maskRevealPos = -1; _maskRevealStart = double.NaN;
            _undo.Clear(); _redo.Clear(); _typingGroup = false;
        }
        Refresh();
        return true;
    }

    /// <summary>Immutable ordinary editor state for accessibility bridges. Offsets are UTF-16;
    /// null selection endpoints mean this field does not currently own the caret.</summary>
    public readonly record struct AccessibleTextState(string Text, int? Anchor, int? Caret, bool ReadOnly);

    /// <summary>Read on the document thread, then publish the immutable result to accessibility
    /// clients. Passwords never expose their content through this API, even during character peek.</summary>
    public AccessibleTextState? GetAccessibleTextState(string path)
    {
        EnsureLaidOut();
        if (NodeAtPath(path) is not { Element: { } el } node || el.GetAttribute("role") != "textbox"
            || el.HasAttribute("data-mask") || el.GetAttribute("data-bind-value") is not { Length: > 0 } bind
            || _model is null) return null;
        var focused = _focusKey == bind && ReferenceEquals(CurrentFocusNode(), node);
        var text = focused ? _editBuffer ?? "" : BindingEngine.Resolve(_model, bind)?.ToString() ?? "";
        return new(text, focused ? Math.Clamp(_selAnchor, 0, text.Length) : null,
            focused ? Math.Clamp(_caret, 0, text.Length) : null,
            IsDisabled(el) || el.HasAttribute("readonly") || el.GetAttribute("aria-readonly") == "true");
    }

    /// <summary>Select an ordinary editor range by path. Reject stale/out-of-range endpoints and
    /// endpoints inside a surrogate pair. Read-only text remains selectable; disabled text does not.</summary>
    public bool AccessibilitySelectText(string path, int anchor, int caret) => Bump(AccessibilitySelectTextCore(path, anchor, caret));

    private bool AccessibilitySelectTextCore(string path, int anchor, int caret)
    {
        if (GetAccessibleTextState(path) is not { } state || NodeAtPath(path)?.Element is not { } el || IsDisabled(el)) return false;
        static bool Boundary(string text, int offset) => offset >= 0 && offset <= text.Length
            && !(offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]));
        if (!Boundary(state.Text, anchor) || !Boundary(state.Text, caret)) return false;
        if (!AccessibilityFocusCore(path)) return false;
        if (HasComposition) CommitCompositionCore(null);
        if (GetAccessibleTextState(path) is not { } current || !Boundary(current.Text, anchor) || !Boundary(current.Text, caret)) return false;
        SetTextSelectionCore(anchor, caret);
        return true;
    }

    /// <summary>Set a slider's value directly — UIA RangeValue.SetValue. Clamps to min/max and writes
    /// through the same binding a drag would, rounded the way a drag rounds.</summary>
    public bool AccessibilitySetValue(string path, double value) => Bump(AccessibilitySetValueCore(path, value));
    private bool AccessibilitySetValueCore(string path, double value)
    {
        EnsureLaidOut();
        if (NodeAtPath(path)?.Element is not { } el) return false;
        if (el.GetAttribute("role") is not "slider") return false;
        // The video seek bar: AT's RangeValue.SetValue seeks the player, seconds in = seconds set.
        if (SeekPlayerOf(el) is { } seekPlayer)
        {
            seekPlayer.Position = Math.Clamp(value, 0, Math.Max(seekPlayer.Duration, 0));
            Refresh();
            return true;
        }
        if (_model is null) return false;
        if (el.GetAttribute("data-bind-value") is not { Length: > 0 } bind) return false;
        var min = ParseAttr(el, "min", 0);
        var max = ParseAttr(el, "max", 100);
        var ok = BindingEngine.TrySet(_model, bind, Math.Round(Math.Clamp(value, min, max)));
        if (ok) Refresh();
        return ok;
    }

    /// <summary>Serialise the semantics tree to an ARIA HTML fragment for the web host's off-screen
    /// screen-reader mirror (the canvas is opaque to assistive tech). See <see cref="Accessibility.AriaHtml"/>.</summary>
    public string BuildAriaHtml(float width, float height) =>
        Accessibility.AriaHtml.Serialize(BuildAccessibilityTree(width, height));

    /// <summary>
    /// Dispatch a click at (x,y): hit-test, run built-in control behaviour (switch
    /// toggle, slider set) and user handlers along the bubble path, write back to the
    /// bound model, and refresh. Returns true if anything handled it (→ needs repaint).
    /// </summary>
    public bool DispatchClick(float x, float y, int clickCount = 1) => Bump(DispatchClickCore(Zc(x), Zc(y), clickCount));
    private bool DispatchClickCore(float x, float y, int clickCount)
    {
        EnsureLaidOut();
        _textDrag = false;

        // An open context menu intercepts the next click: an item runs its command (without
        // blurring the underlying field, so the selection survives for Copy/Cut); a click
        // elsewhere (or on a disabled/separator row) just dismisses it. Either way it's swallowed.
        if (_ctxOpen)
        {
            var ctxHit = HitTesting.HitTest(_root, x, y);
            if (InContextMenu(ctxHit))
            {
                if (ContextCommandOf(ctxHit) is { } cmd) { _ctxOpen = false; ContextRequested?.Invoke(cmd); Refresh(); }
                return true; // inside but not actionable → keep the menu open
            }
            _ctxOpen = false; Refresh(); return true; // outside → dismiss
        }

        // A custom <cupri-context-menu>: a click outside dismisses it; a click on a leaf row closes it
        // AND runs the row's action via the normal flow below; a click on a submenu parent keeps it open.
        if (_ctxCustomIndex >= 0)
        {
            var ctxHit = HitTesting.HitTest(_root, x, y);
            if (!InCustomContextMenu(ctxHit)) { _ctxCustomIndex = -1; Refresh(); return true; } // outside → dismiss
            if (!OnLeafMenuItem(ctxHit)) return true;                                            // parent/padding → keep open
            _ctxCustomIndex = -1;                                                                // leaf → close, then fall through
        }

        var hit = HitTesting.HitTest(_root, x, y);

        // Click-away: close any open bound-flag popup (picker/select/popover) the click landed outside.
        // Doesn't consume the click, so it still does its normal thing; the refresh below applies it.
        var strayClosed = CloseStrayPopups(hit);

        if (hit is null) // click on empty space blurs (and closes any stray popup)
        {
            _kbIndex = -1;
            var blurred = UpdateFocus(null);
            if (strayClosed) Refresh();
            return blurred || strayClosed;
        }

        // Drag surfaces (grips, boundaries, thumbs, handles) grab the pointer before anything
        // else — shared with the touch layer, which must know these drag from the FIRST touch.
        if (TryGrabDragSurface(hit, x, y)) return true;

        // :active press feedback — mark the pressed element chain (restyled below; cleared on pointer-up).
        SetActive(hit.Element);

        // Focus: a click inside a text/number field focuses it; elsewhere blurs.
        RenderNode? field = hit;
        while (field is not null && field.Element?.GetAttribute("role") is not ("textbox" or "spinbutton")) field = field.Parent;
        var focusChanged = UpdateFocus(field?.Element);

        // Sync keyboard focus to the clicked control (so Tab continues from here), but don't
        // show the focus ring for a mouse click — it appears on Tab/Shift-Tab (focus-visible).
        _kbIndex = IndexOfFocusable(hit);
        _focusVisible = false;

        // Built-in behaviour first (steppers, toggles, buttons, user handlers).
        var handled = ActivateFrom(hit, x, y);

        // Clicking a checkbox/radio/switch's text label toggles the control (like <label>).
        if (!handled && ActivateLabel(hit) is { } labelled)
        {
            _kbIndex = FocusableIndexOf(labelled); // continue Tab order from the toggled control
            handled = true;
        }

        // If the click landed in a focused text field and nothing else consumed it, position the
        // caret / select a word (double-click) or line (triple-click), and arm a drag-select.
        if (!handled && field is not null && _focusKey is not null)
        {
            var pos = CaretFromPoint(field, FindCaretAnchor(field) ?? field, x, y);
            var buf = _editBuffer ?? "";
            if (clickCount >= 3) { var (a, b) = LineAt(buf, pos); _selAnchor = a; _caret = b; }
            else if (clickCount == 2) { var (a, b) = WordAt(buf, pos); _selAnchor = a; _caret = b; }
            else { _caret = pos; _selAnchor = pos; _textDrag = true; }
            _caretMoved = true;
            if (focusChanged || strayClosed) Refresh(); // new field or a dismissed popup → rebuild
            ReconcileScope();
            return true;
        }

        if (handled || focusChanged || strayClosed) Refresh();
        else if (_activeChain.Count > 0) ReStyle(); // show the :active press even if nothing else changed
        ReconcileScope(); // a click may have opened/closed an overlay → update the focus scope
        return handled || focusChanged || strayClosed || _activeChain.Count > 0;
    }

    /// <summary>Try to grab a drag surface under the pointer — resize grip, table column boundary,
    /// scrollbar thumb, reorder handle, split divider — starting the drag when one is hit. The
    /// priority order is load-bearing (corner grip beats thumb beats content). Shared by the mouse
    /// path (this IS the old top of DispatchClickCore, verbatim) and the touch layer.</summary>
    private bool TryGrabDragSurface(RenderNode hit, float x, float y)
    {
        // Grabbing a resize grip (bottom-right corner) starts a resize-drag — corner takes priority.
        for (var n = hit; n is not null; n = n.Parent)
            if (InResizeGrip(n, x, y))
            {
                _resizeDrag = n; _resizeX0 = x; _resizeY0 = y;
                _resizeW0 = n.ResizeW ?? n.Width; _resizeH0 = n.ResizeH ?? n.Height;
                return true;
            }

        // Grabbing a resizable table's column boundary (a header cell's right edge) starts a column drag.
        for (var n = hit; n is not null; n = n.Parent)
            if (StartColumnResize(n, x)) return true;

        // Grabbing a scrollbar thumb starts a scroll-drag (takes priority; doesn't focus/blur).
        for (var n = hit; n is not null; n = n.Parent)
            if (ThumbRect(n) is { } tr && x >= tr.X - 6 && x <= tr.X + tr.W + 8 && y >= tr.Y && y <= tr.Y + tr.H)
            {
                _scrollDrag = n; _scrollDragScroll0 = Math.Clamp(n.ScrollY, 0, n.MaxScrollY); _scrollDragY0 = y;
                return true;
            }

        // Grabbing a drag-reorder handle starts a reorder drag (paint-time; doesn't focus/blur).
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element?.ClassList.Contains("cupri-reorder-handle") == true && StartReorder(n, x, y))
                return true;

        // Grabbing a split-pane divider starts a split-resize drag.
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element?.ClassList.Contains("cupri-split-divider") == true && StartSplit(n, x, y))
                return true;

        return false;
    }

    // ---- the touch layer's view of a press (TouchInput; internal, same assembly) --------------

    /// <summary>What a finger landed on, decided WITHOUT side effects — the touch layer defers
    /// activation for everything except dedicated drag surfaces, which drag from the first touch.</summary>
    internal enum PressKind { Empty, DragSurface, TextField, Other }

    internal PressKind ClassifyPress(float xHost, float yHost)
    {
        EnsureLaidOut();
        float x = Zc(xHost), y = Zc(yHost);
        var hit = HitTesting.HitTest(_root, x, y);
        if (hit is null) return PressKind.Empty;

        // Pure mirrors of TryGrabDragSurface's conditions (no drag is started), plus the slider:
        // ActivateFrom starts a slider drag on click, so to a finger it too is a drag surface.
        for (var n = hit; n is not null; n = n.Parent)
        {
            if (InResizeGrip(n, x, y)) return PressKind.DragSurface;
            if (ColumnBoundaryAt(n, x) is not null) return PressKind.DragSurface;
            if (ThumbRect(n) is { } tr && x >= tr.X - 6 && x <= tr.X + tr.W + 8 && y >= tr.Y && y <= tr.Y + tr.H)
                return PressKind.DragSurface;
            if (n.Element?.ClassList.Contains("cupri-reorder-handle") == true) return PressKind.DragSurface;
            if (n.Element?.ClassList.Contains("cupri-split-divider") == true) return PressKind.DragSurface;
            if (n.Element?.GetAttribute("role") == "slider") return PressKind.DragSurface;
        }

        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("role") is "textbox" or "spinbutton") return PressKind.TextField;

        return PressKind.Other;
    }

    /// <summary>Press feedback for a deferred tap: :active on the chain under the finger — and
    /// nothing else. No focus, no activation; those happen at finger-up if the press turns out to
    /// be a tap.</summary>
    internal bool SetPressed(float xHost, float yHost)
    {
        EnsureLaidOut();
        float x = Zc(xHost), y = Zc(yHost);
        var hit = HitTesting.HitTest(_root, x, y);
        if (hit?.Element is null) return false;
        SetActive(hit.Element);
        ReStyle();
        return true;
    }

    /// <summary>The press became a scroll (or was cancelled): drop the :active feedback.</summary>
    internal bool ClearPressed() => ClearActive();

    /// <summary>The structural path of the scroller a drag at (x, y) would move — captured once at
    /// gesture start so a fling can outlive the per-keystroke rebuild (paths survive; node
    /// references do not).</summary>
    internal string? ScrollTargetAt(float xHost, float yHost, bool horizontal = false)
    {
        EnsureLaidOut();
        float x = Zc(xHost), y = Zc(yHost);
        for (var n = HitTesting.HitTest(_root, x, y); n is not null; n = n.Parent)
            if (horizontal ? n.IsScrollableX : n.IsScrollable) return PathOf(n);
        return null;
    }

    // ---- fling (momentum scrolling) ------------------------------------------------------------
    // Lives in the DOCUMENT, not the gesture recognizer, so every host's existing wake gate
    // (HasActiveAnimations) and drive gate (HasActiveTransitions) see it with zero host changes.
    // State is a structural path + a velocity — nothing that can dangle across a rebuild.

    private string? _flingPath;
    private float _flingV;
    private double _flingT = double.NaN;
    private const float FlingDecay = 3f;        // exp decay per second — calm, native-adjacent feel
    private const float FlingStopSpeed = 30f;   // px/s below which the scroll just stops

    /// <summary>True while a momentum scroll is in flight (drives the host frame loop).</summary>
    public bool FlingActive => _flingPath is not null;

    /// <summary>Begin a momentum scroll of the scroller at <paramref name="path"/> (from
    /// <see cref="ScrollTargetAt"/>), at signed pixels/second (positive scrolls down). The
    /// integration happens in <see cref="Animate"/>; a fling deliberately does NOT chain to
    /// ancestor scrollers — momentum dies at the edge, as native platforms do.</summary>
    // ---- overscroll (the rubber band) ----------------------------------------------------------
    // Lives beside the fling for the same reason: it is an animation the hosts already know how to
    // drive, and its state is a structural path plus a number — nothing that dangles on a rebuild.

    private string? _overscrollPath;
    private const float OverscrollMax = 90f;      // how far the band can stretch, logical px
    private const float OverscrollResist = 0.4f;  // …and how much of your drag it converts
    private const float OverscrollSpring = 12f;   // decay rate on release (per second, exponential)
    private bool _overscrollHeld;                 // a finger is still stretching it

    /// <summary>Stretch a scroller past its edge. Called when a drag had nowhere left to go: the
    /// alternative is a dead stop, which gives a finger no signal that it has arrived. Resistance
    /// makes the band feel like a band — the further it is pulled, the less each pixel gives.</summary>
    public bool Overscroll(string path, float pixelDelta) => Overscroll(path, pixelDelta, false);

    /// <inheritdoc cref="Overscroll(string, float)"/>
    /// <param name="horizontal">Stretch the sideways axis instead. A row that scrolls sideways
    /// deserves the same signal at ITS end as the page does at the bottom; without this, reaching
    /// the end of a horizontal strip felt like the app had died while the page still bounced.</param>
    public bool Overscroll(string path, float pixelDelta, bool horizontal)
    {
        if (horizontal)
        {
            if (NodeAtPath(path) is not { IsScrollableX: true } nx) return false;

            var atLeft = nx.ScrollX <= 0.01f && pixelDelta < 0;
            var atRight = nx.ScrollX >= nx.MaxScrollX - 0.01f && pixelDelta > 0;
            if (!atLeft && !atRight && MathF.Abs(nx.OverscrollX) < 0.01f) return false;

            HandOverBandTo(path);
            _overscrollHeld = true;
            var slackX = 1f - MathF.Min(1f, MathF.Abs(nx.OverscrollX) / OverscrollMax);
            nx.OverscrollX = Math.Clamp(nx.OverscrollX + pixelDelta * OverscrollResist * slackX,
                                        -OverscrollMax, OverscrollMax);
            return Bump(true);
        }

        if (NodeAtPath(path) is not { IsScrollable: true } n) return false;

        // Only past an EDGE: pulling back toward the content is ordinary scrolling.
        var atTop = n.ScrollY <= 0.01f && pixelDelta < 0;
        var atBottom = n.ScrollY >= n.MaxScrollY - 0.01f && pixelDelta > 0;
        if (!atTop && !atBottom && MathF.Abs(n.OverscrollY) < 0.01f) return false;

        HandOverBandTo(path);
        _overscrollHeld = true;
        var slack = 1f - MathF.Min(1f, MathF.Abs(n.OverscrollY) / OverscrollMax);
        n.OverscrollY = Math.Clamp(n.OverscrollY + pixelDelta * OverscrollResist * slack,
                                   -OverscrollMax, OverscrollMax);
        return Bump(true);
    }

    /// <summary>The finger let go: let the band spring back.</summary>
    public void ReleaseOverscroll() => _overscrollHeld = false;

    /// <summary>True while a band is stretched or springing — a host's animation gate.</summary>
    public bool OverscrollActive =>
        _overscrollPath is not null && NodeAtPath(_overscrollPath) is { } n && Stretched(n);

    private static bool Stretched(RenderNode n) =>
        MathF.Abs(n.OverscrollY) > 0.05f || MathF.Abs(n.OverscrollX) > 0.05f;

    /// <summary>Only one scroller holds a band at a time. Moving to another must release the last
    /// one by hand: it is no longer stepped, so a band left stretched would freeze at that offset
    /// and shift the content permanently.</summary>
    private void HandOverBandTo(string path)
    {
        if (_overscrollPath is not null && _overscrollPath != path &&
            NodeAtPath(_overscrollPath) is { } prev)
        { prev.OverscrollY = 0; prev.OverscrollX = 0; }
        _overscrollPath = path;
    }

    private bool StepOverscroll(double now)
    {
        if (_overscrollPath is null) return false;
        if (NodeAtPath(_overscrollPath) is not { } n || !Stretched(n))
        {
            if (NodeAtPath(_overscrollPath) is { } done) { done.OverscrollY = 0; done.OverscrollX = 0; }
            _overscrollPath = null;
            return false;
        }
        if (_overscrollHeld) return true;           // still being pulled; nothing to animate

        var dt = double.IsNaN(_overscrollT) ? 0 : Math.Clamp(now - _overscrollT, 0, 0.1);
        _overscrollT = now;
        if (dt <= 0) return true;

        var decay = (float)Math.Exp(-dt * OverscrollSpring);
        n.OverscrollY *= decay;
        n.OverscrollX *= decay;
        if (MathF.Abs(n.OverscrollY) < 0.05f) n.OverscrollY = 0;
        if (MathF.Abs(n.OverscrollX) < 0.05f) n.OverscrollX = 0;
        return true;
    }

    private double _overscrollT = double.NaN;

    public bool StartFling(string path, float pixelsPerSecond, bool horizontal = false)
    {
        if (MathF.Abs(pixelsPerSecond) < FlingStopSpeed) return false;
        _flingPath = path;
        _flingV = pixelsPerSecond;
        _flingHorizontal = horizontal;
        _flingT = double.NaN;
        return true;
    }

    /// <summary>Stop any in-flight momentum scroll (a finger landing mid-fling catches the list).</summary>
    public void StopFling()
    {
        _flingPath = null;
        _flingT = double.NaN;
    }

    private bool _flingHorizontal;

    private bool StepFling(double now)
    {
        if (_flingPath is null) return false;
        var dt = double.IsNaN(_flingT) ? 0 : Math.Clamp(now - _flingT, 0, 0.1);
        _flingT = now;
        if (dt <= 0) return true;                       // first frame stamps the clock only

        _flingV *= (float)Math.Exp(-dt * FlingDecay);
        var n = NodeAtPath(_flingPath);
        if (n is null || !(_flingHorizontal ? n.IsScrollableX : n.IsScrollable)) { StopFling(); return false; }

        bool moved;
        if (_flingHorizontal)
        {
            var beforeX = n.ScrollX;
            n.ScrollX = Math.Clamp(n.ScrollX + _flingV * (float)dt, 0, n.MaxScrollX);
            moved = Math.Abs(n.ScrollX - beforeX) > 0.01f;
        }
        else
        {
            var before = n.ScrollY;
            n.ScrollY = Math.Clamp(n.ScrollY + _flingV * (float)dt, 0, n.MaxScrollY);
            moved = Math.Abs(n.ScrollY - before) > 0.01f;
            RewindowVirtual(n);                         // may rebuild; we hold a path, not the node
        }
        if (!moved || MathF.Abs(_flingV) < FlingStopSpeed) StopFling();
        return moved;
    }

    // Walk from a node up the ancestor chain, applying the first built-in control behaviour or
    // user click handler. Shared by mouse clicks and keyboard activation (Enter/Space).
    private bool ActivateFrom(RenderNode start, float x, float y)
    {
        for (var node = start; node is not null; node = node.Parent)
        {
            if (node.Element is not { } el) continue;

            // Custom registered interaction primitives (extensibility) — checked before the built-ins
            // so a third party can define new data-* behaviours.
            foreach (var (attr, handler) in _actionHandlers)
                if (el.GetAttribute(attr) is { } av && handler(new Interaction.CupriActionEvent(node, el, av, _model, x, y)))
                    return true;

            // A link: an in-page #anchor scrolls its target into view here; any other href raises Navigated
            // (the app routes internal hrefs, a host opens external ones). Keyboard Enter routes here too.
            if (el.LocalName == "a" && el.GetAttribute("href") is { Length: > 0 } href)
            {
                if (href[0] == '#') return ScrollToAnchor(href[1..]);
                Navigated?.Invoke(new Interaction.NavigateEvent(href,
                    Interaction.ExternalLinkPolicy.IsAllowed(href)));
                return true;
            }

            // Number stepper: +/- button adjusts the nearest numeric field's bound value.
            if (el.GetAttribute("data-cupri-step") is { Length: > 0 } stepRaw) return StepNumber(node, stepRaw);

            // Video transport (play/pause toggle, mute) for the nearest enclosing <cupri-video>.
            if (el.GetAttribute("data-video-cmd") is { Length: > 0 } videoCmd) return VideoCommand(node, videoCmd);

            // Window drag: a frameless window has no title bar, so an element can be one. The press
            // only ARMS it — every later move reports its distance from here, and the host adds that
            // to the window's position. Claimed only when a host is listening, so the same markup in
            // a browser leaves the press to whatever else wanted it.
            if (el.HasAttribute("data-window-drag"))
            {
                if (WindowMoveRequested is null) return false;
                _windowDrag = true;
                _windowDragX = x;
                _windowDragY = y;
                return true;
            }

            // Window command (fullscreen…): raised for the host, like Navigated. Handled only when
            // a host actually subscribed — headless/embedded consumers just ignore the click.
            if (el.GetAttribute("data-window-command") is { Length: > 0 } wc)
            {
                if (WindowCommandRequested is not { } windowHandlers) return false;
                windowHandlers(wc switch
                {
                    "enter-fullscreen" => Interaction.WindowCommand.EnterFullscreen,
                    "exit-fullscreen" => Interaction.WindowCommand.ExitFullscreen,
                    _ => Interaction.WindowCommand.ToggleFullscreen,
                });
                return true;
            }

            // Generic "set a bound value" click (tabs, select options, tree selection). Closes any
            // containing overlay so picking an option dismisses its dropdown — unless the element opts
            // out with data-set-keep (an in-place control that adjusts a value without dismissing, e.g.
            // the date picker's month navigation).
            if (el.GetAttribute("data-set-path") is { Length: > 0 } setPath && _model is not null)
            {
                var ok = BindingEngine.TrySet(_model, setPath, el.GetAttribute("data-set-value") ?? "");
                if (!el.HasAttribute("data-set-keep")) SetNearestOpen(node, false);
                return ok;
            }

            // Toggle a value in a bound comma-set (multi-select table rows): add it if absent, else remove it.
            if (el.GetAttribute("data-set-toggle") is { Length: > 0 } togPath && _model is not null)
            {
                var v = el.GetAttribute("data-toggle-value") ?? "";
                var set = (BindingEngine.Resolve(_model, togPath)?.ToString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!set.Remove(v)) set.Add(v);
                return BindingEngine.TrySet(_model, togPath, string.Join(",", set));
            }

            // Overlay open/close: dismiss (backdrop/outside) and trigger toggle.
            if (el.HasAttribute("data-cupri-dismiss")) return SetNearestOpen(node, false);
            if (el.HasAttribute("data-cupri-toggle")) return ToggleNearestOpen(node);

            switch (el.GetAttribute("role"))
            {
                case "switch" or "checkbox" or "radio": return ActivateControl(el);
                case "slider": return StartSliderDrag(node, el, x);
                default:
                    var any = false;
                    foreach (var (_, compiled, handler) in _clickHandlers)
                    {
                        if (!Matches(el, compiled)) continue;
                        handler(new CupriPointerEvent(x, y, node, el));
                        any = true;
                    }
                    if (any) return true;
                    break;
            }
        }
        return false;
    }

    // In-page anchor: scroll the element with this id to the top of its nearest scrollable ancestor.
    // Scroll offsets survive the rebuild (CaptureScroll), so the page stays put at the anchor.
    private bool ScrollToAnchor(string id)
    {
        if (id.Length > 0 && FindById(_root, id) is { } target)
            for (var s = target.Parent; s is not null; s = s.Parent)
            {
                if (!s.IsScrollable) continue;
                var offset = HitTesting.AbsoluteBox(target).Y - HitTesting.AbsoluteBox(s).Y; // unscrolled offset
                s.ScrollY = Math.Clamp(offset - 12f, 0, s.MaxScrollY);                        // 12px above
                break;
            }
        return true; // the link is handled whether or not the anchor exists / anything scrolls
    }

    private static RenderNode? FindById(RenderNode n, string id)
    {
        if (n.Element?.GetAttribute("id") == id) return n;
        foreach (var c in n.Children) { var f = FindById(c, id); if (f is not null) return f; }
        return null;
    }

    // ---- keyboard focus + tab order (a11y capability #4) ---------------------
    // The interactive roles/attributes Tab stops on; an element is focusable if it is one of
    // these AND has no focusable descendant (so we land on the actual control, not a wrapper).
    private static bool IsFocusableRole(IElement el) =>
        el.GetAttribute("role") is "switch" or "checkbox" or "radio" or "slider"
                                or "textbox" or "spinbutton" or "button"
        || (el.LocalName == "a" && el.HasAttribute("href"))
        || el.HasAttribute("data-cupri-toggle")
        || el.HasAttribute("data-set-path")
        || el.HasAttribute("data-set-toggle")
        || el.HasAttribute("data-cupri-step");

    private bool IsFocusable(IElement el) =>
        IsFocusableRole(el) || _clickHandlers.Exists(h => Matches(el, h.Compiled));

    // Focusable render nodes in DOM (pre-order) order; skips a matched node's subtree so a
    // control counts once. display:none subtrees are already absent from the render tree. When
    // an overlay is open its panel is a focus scope — Tab is trapped within it (a11y).
    private List<RenderNode> Focusables()
    {
        var scope = FocusScope() ?? _root;
        var list = new List<RenderNode>();
        void Walk(RenderNode n)
        {
            if (n.Element is { } el && IsFocusable(el)) { list.Add(n); return; }
            foreach (var c in n.Children) Walk(c);
        }
        foreach (var c in scope.Children) Walk(c);
        return list;
    }

    // The top-most open overlay panel (marked data-focus-scope), or null when none is open.
    private RenderNode? FocusScope()
    {
        RenderNode? scope = null;
        void Walk(RenderNode n)
        {
            if (n.Element?.HasAttribute("data-focus-scope") == true) scope = n; // last wins → topmost
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        return scope;
    }

    // Keep keyboard focus consistent as overlays open/close: entering a scope focuses its first
    // control; leaving clears focus. Called after any state change that may open/close an overlay.
    private bool _overlayFocused;
    private void ReconcileScope()
    {
        var scoped = FocusScope() is not null;
        if (scoped != _overlayFocused) _kbIndex = scoped ? 0 : -1;
        _overlayFocused = scoped;
    }

    private RenderNode? CurrentFocusNode()
    {
        var f = Focusables();
        return _kbIndex >= 0 && _kbIndex < f.Count ? f[_kbIndex] : null;
    }

    private int IndexOfFocusable(RenderNode hit)
    {
        // The focusable ancestor of the hit node (or itself), matched against the current list.
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element is { } el && IsFocusable(el))
            {
                var f = Focusables();
                for (var i = 0; i < f.Count; i++) if (ReferenceEquals(f[i], n)) return i;
                return -1;
            }
        return -1;
    }

    // Index of the focusable whose element is `el` (for syncing Tab order after a label click).
    private int FocusableIndexOf(IElement el)
    {
        var f = Focusables();
        for (var i = 0; i < f.Count; i++) if (ReferenceEquals(f[i].Element, el)) return i;
        return -1;
    }

    /// <summary>Move keyboard focus to the next (dir=+1) or previous (dir=-1) control, wrapping.</summary>
    private bool MoveFocus(int dir)
    {
        var f = Focusables();
        if (f.Count == 0) return false;
        _kbIndex = _kbIndex < 0 || _kbIndex >= f.Count
            ? (dir > 0 ? 0 : f.Count - 1)
            : (_kbIndex + dir + f.Count) % f.Count;
        _focusVisible = true;
        // If the newly focused control is a text field, start editing it; otherwise blur text.
        var el = f[_kbIndex].Element;
        UpdateFocus(el?.GetAttribute("role") is "textbox" or "spinbutton" ? el : null);
        Refresh();
        RevealKeyboardFocus();
        return true;
    }

    private void RevealKeyboardFocus()
    {
        EnsureLaidOut();
        if (CurrentFocusNode() is not { } focused) return;
        RevealControl(PathOf(focused));
    }

    private void RevealControl(string target)
    {
        EnsureLaidOut();
        if (NodeAtPath(target) is not { } focused) return;
        var ancestors = new List<string>();
        for (var child = focused; !child.IsTopLayer && child.Parent is { } parent; child = parent)
            if (parent.IsScrollable || parent.IsScrollableX) ancestors.Add(PathOf(parent));
        foreach (var path in ancestors)
        {
            EnsureLaidOut();
            if (NodeAtPath(target) is not { } current || NodeAtPath(path) is not { } parent) continue;
            var box = HitTesting.ScreenBox(current);
            var viewport = HitTesting.ScreenBox(parent);
            // Include the focus outline. Oversized controls align their leading edge.
            static float Delta(float start, float size, float viewStart, float viewSize)
            {
                start -= 4; size += 8;
                return start < viewStart ? start - viewStart : MathF.Max(0, MathF.Min(start - viewStart, start + size - viewStart - viewSize));
            }
            var dx = parent.IsScrollableX ? Delta(box.X, box.W, viewport.X + parent.ContentLeftInset, parent.ContentBoxWidth) : 0;
            var dy = parent.IsScrollable ? Delta(box.Y, box.H, viewport.Y + parent.ContentTopInset, parent.ContentBoxHeight) : 0;
            if (dx != 0 || dy != 0) ScrollCaptured(dy != 0 ? path : null, dx != 0 ? path : null, dy, dx);
        }
    }

    // Activate the keyboard-focused control (Enter/Space) as if clicked at its centre.
    private bool ActivateFocused()
    {
        var f = Focusables();
        if (_kbIndex < 0 || _kbIndex >= f.Count) return false;
        var (bx, by, bw, bh) = HitTesting.AbsoluteBox(f[_kbIndex]);
        var handled = ActivateFrom(f[_kbIndex], bx + bw / 2, by + bh / 2);
        if (handled) { Refresh(); ReconcileScope(); }
        return handled;
    }

    // Arrow-key nav within a group: radios move+select among their group; everything else
    // (menu items, list options, general) moves focus to the previous/next focusable. Sliders
    // are handled separately (they nudge their value) before this is called.
    private bool ArrowMove(int dir)
    {
        if (CurrentFocusNode() is { } cur && cur.Element?.GetAttribute("role") == "radio")
            return RadioArrow(cur, dir);
        return MoveFocus(dir);
    }

    // Move to the previous/next radio in the same group and select it (ARIA radio pattern).
    private bool RadioArrow(RenderNode cur, int dir)
    {
        var group = cur.Element!.GetAttribute("data-bind-group");
        var f = Focusables();
        var radios = f.Where(n => n.Element?.GetAttribute("role") == "radio"
                                  && n.Element.GetAttribute("data-bind-group") == group).ToList();
        if (radios.Count < 2) return false;
        var pos = radios.FindIndex(n => ReferenceEquals(n, cur));
        var next = radios[(pos + dir + radios.Count) % radios.Count];
        _kbIndex = f.FindIndex(n => ReferenceEquals(n, next));
        _focusVisible = true;
        var ok = group is { Length: > 0 } && _model is not null
            && BindingEngine.TrySet(_model, group, next.Element!.GetAttribute("value"));
        Refresh();
        return ok;
    }

    // Arrow-nudge a focused slider by its step (or 1/20th of range), clamped to min/max.
    private bool NudgeSlider(RenderNode node, int dir)
    {
        var el = node.Element!;
        // The video seek bar: arrows scrub the PLAYER — 5 s steps, the players' convention.
        if (SeekPlayerOf(el) is { } seekPlayer)
        {
            seekPlayer.Position = Math.Clamp(seekPlayer.Position + dir * 5, 0, Math.Max(seekPlayer.Duration, 0));
            _focusVisible = true;
            Refresh();
            return true;
        }
        if (el.GetAttribute("data-bind-value") is not { Length: > 0 } path || _model is null) return false;
        var min = double.TryParse(el.GetAttribute("min"), out var mn) ? mn : 0;
        var max = double.TryParse(el.GetAttribute("max"), out var mx) ? mx : 100;
        var step = double.TryParse(el.GetAttribute("step"), out var s) && s > 0 ? s : Math.Max(1, (max - min) / 20);
        var cur = BindingEngine.Resolve(_model, path) is { } v && double.TryParse(v.ToString(), out var d) ? d : min;
        var next = Math.Clamp(cur + dir * step, min, max);
        var text = next == Math.Floor(next) ? ((long)next).ToString() : next.ToString();
        _focusVisible = true;
        var ok = BindingEngine.TrySet(_model, path, text);
        Refresh();
        return ok;
    }

    // Escape: close the top-most open overlay if any; otherwise blur a focused text field.
    private bool HandleEscape(KeyMods mods)
    {
        RenderNode? target = null;
        void Walk(RenderNode n)
        {
            if (n.Element?.GetAttribute("data-bind-open") is { Length: > 0 } p
                && _model is not null && BindingEngine.Resolve(_model, p) as bool? == true) target = n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        if (target?.Element?.GetAttribute("data-bind-open") is { Length: > 0 } path && _model is not null)
        {
            BindingEngine.TrySet(_model, path, false);
            _overlayFocused = false; _kbIndex = -1;
            Refresh();
            return true;
        }
        // No overlay to dismiss: Escape ends video fullscreen (element AND window, like the web).
        if (_fullscreenVideo is not null)
        {
            _fullscreenVideo = null;
            WindowCommandRequested?.Invoke(Interaction.WindowCommand.ExitFullscreen);
            Refresh();
            return true;
        }
        // An app-registered Escape shortcut sits here: below everything the engine dismisses itself (the
        // context menu above, an open overlay and video fullscreen already returned), and above the plain
        // blur — because "cancel this edit" is wanted precisely while the field still has focus, which is
        // the one case the general plain-key rule (_focusKey is null) would have refused (#88).
        if (_shortcuts.TryGetValue(ShortcutKey(mods, "escape"), out var esc)) { esc(); Refresh(); ReconcileScope(); return true; }

        if (_focusKey is not null) { UpdateFocus(null); Refresh(); return true; }
        return false;
    }

    // The option rows of the focused field's listbox (a data-listbox field, e.g. the combobox), for
    // keyboard nav; null if the focused field isn't a listbox.
    private List<IElement>? FocusedListbox()
    {
        if (_dom is null || _focusKey is null) return null;
        var input = _dom.QuerySelector($"[data-bind-value=\"{_focusKey}\"]");
        if (input is null || !input.HasAttribute("data-listbox") || input.ParentElement is not { } container) return null;
        var opts = new List<IElement>();
        foreach (var o in container.QuerySelectorAll("[role=\"option\"][data-set-value]")) opts.Add(o);
        return opts;
    }

    // An open [data-gridnav] overlay (the date picker's day grid) + its option cells and column count,
    // for arrow-key 2D navigation; null if none is open.
    private (List<IElement> Cells, int Cols)? FocusedGrid()
    {
        if (_dom is null || _dom.QuerySelector("[data-gridnav]") is not { } grid) return null;
        var cols = int.TryParse(grid.GetAttribute("data-gridnav"), out var c) ? Math.Max(1, c) : 7;
        var cells = new List<IElement>();
        foreach (var cell in grid.QuerySelectorAll("[data-set-value]")) cells.Add(cell);
        return cells.Count > 0 ? (cells, cols) : null;
    }

    private static int GridSelectedIndex(List<IElement> cells)
    {
        for (var i = 0; i < cells.Count; i++) if (cells[i].ClassList.Contains("selected")) return i;
        return 0;
    }

    private bool MoveGrid((List<IElement> Cells, int Cols) g, int delta)
    {
        _gridHi = Math.Clamp(_gridHi + delta, 0, g.Cells.Count - 1); // clamp keeps it within the shown month
        foreach (var cell in g.Cells) cell.RemoveAttribute("data-highlight");
        g.Cells[_gridHi].SetAttribute("data-highlight", "");
        ReStyle();
        return true;
    }

    private bool ActivateGrid((List<IElement> Cells, int Cols) g)
    {
        if (_gridHi < 0 || _gridHi >= g.Cells.Count || _model is null) return false;
        var cell = g.Cells[_gridHi];
        if (cell.GetAttribute("data-set-path") is not { Length: > 0 } path) return false;
        BindingEngine.TrySet(_model, path, cell.GetAttribute("data-set-value") ?? "");
        for (var e = cell; e is not null; e = e.ParentElement) // close the overlay (set its bound open=false)
            if (e.GetAttribute("data-bind-open") is { Length: > 0 } op) { BindingEngine.TrySet(_model, op, "false"); break; }
        _gridHi = -1;
        Refresh();
        return true;
    }

    private bool UpdateFocus(IElement? field)
    {
        var key = field?.GetAttribute("data-bind-value") ?? field?.GetAttribute("id");
        if (key == _focusKey) return false;
        if (HasComposition) CommitCompositionCore(null); // focus is leaving: the preedit commits
        CommitBuffer(); // blur the previous field: validate + commit (or revert) its edit buffer
        _focusKey = key;
        _focusNumeric = field?.HasAttribute("data-numeric") == true;
        _focusMultiline = field?.HasAttribute("data-multiline") == true;
        _focusMask = field?.HasAttribute("data-mask") == true;
        _focusSubmitOnEnter = field?.HasAttribute("data-submit-on-enter") == true;
        _focusTagList = field?.GetAttribute("data-tag-list");
        // The same vocabulary the web platform uses, so one authored attribute drives Android's
        // EditorInfo and the web host's inputmode/enterkeyhint alike.
        _focusInputMode = field?.GetAttribute("inputmode") ?? "";
        _focusEnterHint = field?.GetAttribute("enterkeyhint") ?? "";
        _focusPlaceholder = field?.GetAttribute("placeholder") ?? "";
        _maskRevealPos = -1; _maskRevealStart = double.NaN; // the last-typed peek is per-field
        _focusMin = double.TryParse(field?.GetAttribute("data-min"), out var mn) ? mn : null;
        _focusMax = double.TryParse(field?.GetAttribute("data-max"), out var mx) ? mx : null;
        _focusRequired = field?.HasAttribute("required") == true;
        _focusPattern = field?.GetAttribute("pattern");
        _focusMinLen = int.TryParse(field?.GetAttribute("minlength"), out var mlen) ? mlen : null;
        _editBuffer = key is null ? null : BindingEngine.Resolve(_model, key)?.ToString() ?? "";
        _caret = _editBuffer?.Length ?? 0;
        _selAnchor = _caret;
        _caretMoved = true;
        _listHi = -1; // listbox highlight is per-field
        _undo.Clear(); _redo.Clear(); _typingGroup = false; // undo history is per-field
        // The host's cue to show/hide the soft keyboard. Built without layout (mid-dispatch the
        // tree may be dirty): the kind flags are what show/hide needs; rects come from polling.
        TextInputStateChanged?.Invoke(GetTextInputState());
        return true;
    }

    // ---- IME composition (preedit) -------------------------------------------------------------
    // The engine-side half of soft-keyboard IMEs (Android InputConnection, the browser's
    // composition events, SDL_TEXTEDITING). The preedit lives INSIDE the edit buffer as a marked
    // range: rendering, wrapping and caret math need no second text source, and the permissive-
    // buffer contract holds by construction — the model only ever sees committed text.

    /// <summary>True while an IME composition (preedit) is in progress in the focused field.</summary>
    public bool HasComposition => _compStart >= 0;

    /// <summary>Move the caret / selection to an absolute range in the focused field's text, in
    /// UTF-16 units — the same units Android and the DOM count in, so an IME's offsets need no
    /// translation. This is how a soft keyboard moves the cursor: swiping the spacebar, tapping to
    /// reposition, selecting a word to correct. Out-of-range values clamp rather than fail, because
    /// an IME's model of the text can legitimately lag ours by a frame.</summary>
    public bool SetTextSelection(int start, int end) => Bump(SetTextSelectionCore(start, end));

    private bool SetTextSelectionCore(int start, int end)
    {
        if (_focusKey is null) return false;
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        var s = Math.Clamp(start, 0, value.Length);
        var e = Math.Clamp(end, 0, value.Length);
        if (s == _selAnchor && e == _caret) return false;

        _selAnchor = s;
        _caret = e;
        _caretMoved = true;
        _maskRevealPos = -1;              // moving the caret is not a fresh keystroke
        Refresh();
        return true;
    }

    /// <summary>Mark an already-committed range as the composition — what a keyboard asks for when
    /// you tap a finished word to correct it. The text is unchanged; it simply becomes preedit, so
    /// the next <see cref="SetComposition"/> replaces exactly that range. Returns false when the
    /// range is empty or there is nothing focused.</summary>
    public bool SetComposingRegion(int start, int end) => Bump(SetComposingRegionCore(start, end));

    private bool SetComposingRegionCore(int start, int end)
    {
        if (_focusKey is null) return false;
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        var s = Math.Clamp(Math.Min(start, end), 0, value.Length);
        var e = Math.Clamp(Math.Max(start, end), 0, value.Length);
        if (e <= s) return false;

        // The buffer becomes authoritative (the region is now preedit), and the undo snapshot is
        // taken here so correcting a word is still ONE undo step, as any other composition is.
        _compUndo = (value, _caret, _selAnchor);
        _editBuffer = value;
        _compStart = s;
        _compLen = e - s;
        _caret = e;
        _selAnchor = e;
        _caretMoved = true;
        Refresh();
        return true;
    }

    /// <summary>Begin or update the composition: the marked text becomes <paramref name="text"/>,
    /// with the caret placed <paramref name="caretInComposition"/> UTF-16 units into it (or at its
    /// end when negative). Starting a composition replaces any selection, exactly as typing would.</summary>
    public bool SetComposition(string text, int caretInComposition = -1) => Bump(SetCompositionCore(text, caretInComposition));

    private bool SetCompositionCore(string text, int caretInComposition)
    {
        if (_focusKey is null) return false;
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        if (!_focusMultiline) text = text.Replace('\n', ' ');

        if (_compStart < 0)
        {
            // New composition: snapshot for the single undo step, then replace the selection.
            var selS = Math.Min(_selAnchor, _caret);
            var selE = Math.Max(_selAnchor, _caret);
            selS = Math.Clamp(selS, 0, value.Length);
            selE = Math.Clamp(selE, 0, value.Length);
            _compUndo = (value, _caret, _selAnchor);
            if (selE > selS) value = value.Remove(selS, selE - selS);
            _compStart = selS;
        }
        else
        {
            value = value.Remove(_compStart, Math.Min(_compLen, value.Length - _compStart));
        }

        value = value.Insert(_compStart, text);
        _compLen = text.Length;
        _editBuffer = value;
        _caret = _compStart + (caretInComposition < 0 ? text.Length : Math.Clamp(caretInComposition, 0, text.Length));
        _selAnchor = _caret;
        _caretMoved = true;
        _maskRevealPos = -1;                       // a composition is not a single fresh keystroke
        Refresh();                                 // preedit renders like text — because it IS text
        return true;
    }

    /// <summary>Commit the composition — optionally replacing the preedit with the IME's
    /// <paramref name="finalText"/> — writing through the normal permissive-buffer validation and
    /// recording exactly one undo step for the whole composition. With no composition in progress
    /// a non-null <paramref name="finalText"/> inserts as ordinary typed text.</summary>
    public bool CommitComposition(string? finalText = null) => Bump(CommitCompositionCore(finalText));

    private bool CommitCompositionCore(string? finalText)
    {
        if (_compStart < 0) return finalText is { Length: > 0 } && DispatchKeyCore(finalText, EditKey.None, KeyMods.None);
        if (finalText is not null && finalText != CompositionText()) SetCompositionCore(finalText, -1);

        var undo = _compUndo;
        _compStart = -1; _compLen = 0; _compUndo = null;
        if (undo is { } u && _editBuffer != u.Value)
        {
            _undo.Add(new EditState(u.Value, u.Caret, u.Anchor));
            if (_undo.Count > 300) _undo.RemoveAt(0);
            _redo.Clear();
            _typingGroup = false;
        }
        if (_editBuffer is { } v && _focusKey is { } fk && BufferValid(v)) BindingEngine.TrySet(_model, fk, v);
        Refresh();
        return true;
    }

    /// <summary>Abandon the composition: the preedit text vanishes and the buffer returns to its
    /// pre-composition state. (What Escape does mid-composition.)</summary>
    public bool ClearComposition() => Bump(ClearCompositionCore());

    private bool ClearCompositionCore()
    {
        if (_compStart < 0) return false;
        if (_compUndo is { } u) { _editBuffer = u.Value; _caret = u.Caret; _selAnchor = u.Anchor; }
        else if (_editBuffer is { } v) { _editBuffer = v.Remove(_compStart, Math.Min(_compLen, v.Length - _compStart)); _caret = _selAnchor = _compStart; }
        _compStart = -1; _compLen = 0; _compUndo = null;
        _caretMoved = true;
        Refresh();
        return true;
    }

    private string CompositionText() =>
        _compStart >= 0 && _editBuffer is { } v ? v.Substring(_compStart, Math.Min(_compLen, v.Length - _compStart)) : "";

    // ---- text-input state (what a host's IME layer reads) --------------------------------------

    /// <summary>Raised when text-input focus changes (a field gained or lost focus) — the host's
    /// cue to show or hide the soft keyboard and restart its input connection. Caret movement
    /// within a field is NOT evented; poll <see cref="GetTextInputState"/> after dirty frames.</summary>
    public event Action<Interaction.TextInputState>? TextInputStateChanged;

    /// <summary>The focused field's identity, kind, content, selection and caret rectangle
    /// (logical px), for IME integration. Snapshot semantics: value-typed, safe to hand across
    /// threads.</summary>
    public Interaction.TextInputState GetTextInputState()
    {
        if (_focusKey is null) return default;
        var field = _layoutDirty ? null : FindFocused(_root);
        var value = _editBuffer ?? "";
        return new Interaction.TextInputState(
            Focused: true,
            Role: field?.Element?.GetAttribute("role"),
            Numeric: _focusNumeric,
            Multiline: _focusMultiline,
            Masked: _focusMask,
            Value: value,
            SelStart: Math.Min(_selAnchor, _caret),
            SelEnd: Math.Max(_selAnchor, _caret),
            Composing: HasComposition,
            CaretRect: _layoutDirty ? null : ComputeCaretRect(),
            InputMode: _focusInputMode,
            EnterKeyHint: _focusEnterHint,
            Placeholder: _focusPlaceholder);
    }

    /// <summary>
    /// Feed a keystroke to the focused text field: printable text via <paramref name="text"/>,
    /// or an editing key (backspace/arrows/…). Edits the bound string and refreshes.
    /// </summary>
    public bool DispatchKey(string? text, EditKey key, KeyMods mods = KeyMods.None) => Bump(DispatchKeyCore(text, key, mods));
    private bool DispatchKeyCore(string? text, EditKey key, KeyMods mods)
    {
        // An in-flight IME composition owns the next keystroke: Escape abandons the preedit and is
        // swallowed (the IME's own cancel), anything else commits it first and then acts normally
        // (typing mid-composition commits-then-inserts; Enter commits, then commits the field).
        if (HasComposition)
        {
            if (key == EditKey.Escape) return ClearCompositionCore();
            CommitCompositionCore(null);
        }

        ReconcileScope(); // reflect any overlay that opened/closed since the last event

        // Any keystroke dismisses an open context menu; Escape only closes it (swallowed).
        if (_ctxOpen || _ctxCustomIndex >= 0) { _ctxOpen = false; _ctxCustomIndex = -1; Refresh(); if (key == EditKey.Escape) return true; }

        // A registered keyboard shortcut consumes the key. A Ctrl/Cmd chord fires anywhere; a plain-key
        // shortcut only when no text field is focused (so it doesn't eat normal typing). An unbound Ctrl
        // chord is swallowed too (returns false → the host can defer to the browser) rather than typed.
        // A named key (Enter, Tab, an arrow…) arrives as an EditKey with no text at all, so it is looked
        // up by name. Gating this block on the text being one character made it unreachable for those,
        // which left every OnShortcut(…, "Enter") registered-but-dead — and dead identically to a working
        // one, since the registration itself succeeded (#88). Escape is deliberately not matched here: it
        // is consulted inside HandleEscape, deep enough that an open menu, overlay or video fullscreen
        // still wins over an app's binding.
        var chord = text is { Length: 1 } ? text : key == EditKey.Escape ? null : NameOf(key);
        if (chord is not null && (mods.HasFlag(KeyMods.Ctrl) || _focusKey is null || ArrowFreeWhileEditing(key)))
        {
            // Refresh: the handler almost certainly mutated the model (open a palette, toggle a panel) —
            // without the rebuild the change only became visible on the NEXT event's ReconcileScope,
            // which desktop's constant mouse-moves masked and the web host's quiet keyboard didn't.
            if (_shortcuts.TryGetValue(ShortcutKey(mods, chord), out var shortcut)) { shortcut(); Refresh(); ReconcileScope(); return true; }
            // Never insert a Ctrl/Cmd + letter as text. Only text can be inserted, so an unbound Ctrl +
            // named key must fall through to its ordinary handling below rather than be swallowed here —
            // Ctrl+Enter still activates a focused control when nothing bound it.
            if (mods.HasFlag(KeyMods.Ctrl) && text is not null) return false;
        }

        // Normalize pasted/typed line endings to '\n' (a textarea's internal newline). Windows
        // clipboards deliver "\r\n"; the stray '\r' would otherwise render as a collapsed empty line.
        if (text is { } t && t.IndexOf('\r') >= 0) text = t.Replace("\r\n", "\n").Replace('\r', '\n');

        if (key == EditKey.Escape) return HandleEscape(mods);
        // Tab moves keyboard focus regardless of edit state (trapped within an open overlay).
        if (key == EditKey.Tab) return MoveFocus(+1);
        if (key == EditKey.ShiftTab) return MoveFocus(-1);

        // With no text field focused, Enter/Space activate and arrows navigate groups/sliders.
        // (Hosts deliver space as either EditKey.Space or a " " character.)
        if (_focusKey is null)
        {
            // An open [data-gridnav] overlay (the date picker) captures the arrows for 2D day nav,
            // Enter/Space to pick the highlighted day, ±1 = day, ±cols = week.
            if (key is EditKey.Left or EditKey.Right or EditKey.Up or EditKey.Down or EditKey.Enter or EditKey.Space
                && FocusedGrid() is { } grid)
            {
                if (_gridHi < 0) _gridHi = GridSelectedIndex(grid.Cells);
                return key switch
                {
                    EditKey.Left => MoveGrid(grid, -1),
                    EditKey.Right => MoveGrid(grid, +1),
                    EditKey.Up => MoveGrid(grid, -grid.Cols),
                    EditKey.Down => MoveGrid(grid, +grid.Cols),
                    _ => ActivateGrid(grid), // Enter / Space
                };
            }

            var focused = CurrentFocusNode();
            var role = focused?.Element?.GetAttribute("role");

            // Reorder: ↑/↓ on a focused grip moves the row a slot (the keyboard equivalent of the drag).
            if (focused?.Element?.ClassList.Contains("cupri-reorder-handle") == true && key is EditKey.Up or EditKey.Down)
                return KeyboardReorder(focused, key == EditKey.Up ? -1 : +1);
            // Tree: →/← expand/collapse a focused tree item (ARIA tree pattern).
            if (focused?.Element?.ClassList.Contains("cupri-tree-twist") == true && key is EditKey.Left or EditKey.Right)
                return TreeExpand(focused, key == EditKey.Right);

            switch (key)
            {
                case EditKey.Enter or EditKey.Space: return ActivateFocused();
                case EditKey.Up: return role == "slider" ? NudgeSlider(CurrentFocusNode()!, +1) : ArrowMove(-1);
                case EditKey.Down: return role == "slider" ? NudgeSlider(CurrentFocusNode()!, -1) : ArrowMove(+1);
                case EditKey.Left: return role == "slider" ? NudgeSlider(CurrentFocusNode()!, -1) : ArrowMove(-1);
                case EditKey.Right: return role == "slider" ? NudgeSlider(CurrentFocusNode()!, +1) : ArrowMove(+1);
            }
            return text == " " && ActivateFocused();
        }

        if (_model is null) return false;

        // Combobox / focused-listbox keyboard nav: Down/Up move a highlight over the suggestion rows,
        // Enter commits the highlighted one. (Applies only to a focused field marked data-listbox.)
        if (key is EditKey.Down or EditKey.Up or EditKey.Enter && FocusedListbox() is { } lb)
        {
            if (key is EditKey.Down or EditKey.Up)
            {
                if (lb.Count == 0) return false;
                foreach (var o in lb) o.RemoveAttribute("data-highlight");
                _listHi = key == EditKey.Down
                    ? Math.Min(_listHi < 0 ? 0 : _listHi + 1, lb.Count - 1)
                    : Math.Max(_listHi < 0 ? 0 : _listHi - 1, 0);
                lb[_listHi].SetAttribute("data-highlight", "");
                ReStyle();
                return true;
            }
            if (_listHi >= 0 && _listHi < lb.Count && lb[_listHi].GetAttribute("data-set-path") is { Length: > 0 } sp)
            {
                var row = lb[_listHi];                     // capture before the blur below resets _listHi
                var picked = row.GetAttribute("data-set-value") ?? "";
                UpdateFocus(null);                         // blur (commits the typed buffer)…
                BindingEngine.TrySet(_model, sp, picked);  // …then overwrite with the chosen suggestion
                // Close the enclosing overlay if the row lives in one (a command palette) — mirrors the
                // click path's SetNearestOpen; a plain combobox has no data-bind-open ancestor, so this no-ops.
                for (var e = row; e is not null; e = e.ParentElement)
                    if (e.GetAttribute("data-bind-open") is { Length: > 0 } op) { BindingEngine.TrySet(_model, op, false); break; }
                Refresh();
                return true;
            }
        }

        // Edit a permissive buffer (may hold an invalid value while typing); it shows a red
        // border and is validated/clamped on blur — never block the user mid-edit.
        var value = _editBuffer ?? BindingEngine.Resolve(_model, _focusKey)?.ToString() ?? "";
        var caret = Math.Clamp(_caret, 0, value.Length);
        var anchor = Math.Clamp(_selAnchor, 0, value.Length);
        var shift = mods.HasFlag(KeyMods.Shift);
        var ctrl = mods.HasFlag(KeyMods.Ctrl);
        int selS = Math.Min(anchor, caret), selE = Math.Max(anchor, caret);
        var hasSel = selS != selE;
        var edited = false;
        var (oValue, oCaret, oAnchor) = (value, caret, anchor); // pre-edit snapshot (for undo)

        switch (key)
        {
            case EditKey.SelectAll: anchor = 0; caret = value.Length; break;

            case EditKey.Left:
                if (!shift && hasSel) caret = selS;                                        // collapse to left edge
                else caret = ctrl ? WordLeft(value, caret) : caret - StepBack(value, caret);
                if (!shift) anchor = caret;
                break;
            case EditKey.Right:
                if (!shift && hasSel) caret = selE;
                else caret = ctrl ? WordRight(value, caret) : caret + StepForward(value, caret);
                if (!shift) anchor = caret;
                break;
            case EditKey.Home: caret = 0; if (!shift) anchor = caret; break;
            case EditKey.End: caret = value.Length; if (!shift) anchor = caret; break;

            case EditKey.Backspace
                when _focusTagList is { Length: > 0 } backPath && (_editBuffer ?? "").Length == 0 && !hasSel:
                // The tag-box idiom: Backspace on an EMPTY entry takes back the last chip, so a
                // mistyped tag is undone where the hand already is instead of by hunting for its ×.
                // Gated on the entry being empty — while there is text to delete, Backspace deletes
                // text, and eating a chip mid-word would be its own small disaster.
                if (RemoveLastTag(backPath)) Refresh();
                return true;

            case EditKey.Backspace:
                if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; edited = true; }
                else if (ctrl && caret > 0) { var w = WordLeft(value, caret); value = value.Remove(w, caret - w); caret = w; edited = true; }
                else if (caret > 0) { var n = StepBack(value, caret); value = value.Remove(caret - n, n); caret -= n; edited = true; }
                anchor = caret;
                break;
            case EditKey.Delete:
                if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; edited = true; }
                else if (ctrl && caret < value.Length) { var w = WordRight(value, caret); value = value.Remove(caret, w - caret); edited = true; }
                else if (caret < value.Length) { value = value.Remove(caret, StepForward(value, caret)); edited = true; }
                anchor = caret;
                break;

            case EditKey.Enter:
                // "Enter sends, Shift+Enter starts a new line" (#90). Shift always inserts.
                //
                // A SINGLE-LINE field tries this without asking, which is what <input> does inside a
                // <form>: Enter there has no other meaning worth keeping — it committed and blurred,
                // which is quieter and less useful than submitting. A TEXTAREA must opt in with
                // submit-on-enter, because Enter already means newline and taking that by default
                // would eat a keystroke in every other textarea on the page.
                //
                // There is no <form> element in this engine, so the scope is the attribute OnSubmit
                // bubbles to: an ancestor carrying data-… IS the form boundary, and a field with no
                // such ancestor claims nothing and behaves exactly as before.
                //
                // Commit BEFORE raising: the handler's whole job is to read the value that was just
                // typed. Focus is deliberately kept — a composer goes on composing after it sends.
                // Nothing claimed it → fall through to the ordinary Enter below, so an unhandled
                // field behaves as it did rather than swallowing the key.
                // A tag field claims Enter before any submit does: in a "type a tag, press Enter"
                // control the key means "commit this tag", and letting it submit the surrounding form
                // instead would send a half-filled form every time someone added a tag.
                if (_focusTagList is { Length: > 0 } tagPath && !shift)
                {
                    var entry = (_editBuffer ?? "").Trim();
                    if (entry.Length == 0) return true;              // Enter on an empty tag box: no-op
                    if (AppendTag(tagPath, entry)) { _editBuffer = ""; _caret = _selAnchor = 0; Refresh(); }
                    return true;
                }
                if ((_focusSubmitOnEnter || !_focusMultiline) && !shift)
                {
                    var pending = _editBuffer;
                    CommitBuffer();
                    if (RaiseSubmit()) { Refresh(); ReconcileScope(); return true; }
                    _editBuffer = pending;                     // unclaimed: put the edit back, untouched
                }
                if (_focusMultiline)
                {
                    if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; }
                    value = value.Insert(caret, "\n"); caret++; anchor = caret; edited = true; break;
                }
                CommitBuffer(); _focusKey = null; Refresh(); return true; // validate + commit + blur

            default:
                if (!string.IsNullOrEmpty(text))
                {
                    // A single-line field takes no hard line breaks (like <input>): a pasted multi-line
                    // string collapses its newlines to spaces so it stays one logical line.
                    if (!_focusMultiline && text.IndexOf('\n') >= 0) text = text.Replace('\n', ' ');
                    if (hasSel) { value = value.Remove(selS, selE - selS); caret = selS; }
                    value = value.Insert(caret, text); caret += text.Length; anchor = caret; edited = true;
                }
                break;
        }

        // Caret/delete arithmetic is CODE-POINT aware: an emoji is two UTF-16 units, and stepping
        // one unit would split the surrogate pair into mojibake the model then commits.
        static int StepBack(string v, int at) =>
            at >= 2 && char.IsLowSurrogate(v[at - 1]) && char.IsHighSurrogate(v[at - 2]) ? 2 : at > 0 ? 1 : 0;
        static int StepForward(string v, int at) =>
            at + 1 < v.Length && char.IsHighSurrogate(v[at]) && char.IsLowSurrogate(v[at + 1]) ? 2 : at < v.Length ? 1 : 0;

        // Mobile-style peek: a masked field briefly shows the character you just typed, then re-masks
        // it (Animate expires the peek). Any other edit — delete, navigation, multi-char paste —
        // hides it immediately so only a single fresh keystroke is ever visible.
        if (_focusMask)
        {
            if (edited && key == EditKey.None && text is not null && IsSingleCodePoint(text)) { _maskRevealPos = caret - text.Length; _maskRevealStart = double.NaN; }
            else _maskRevealPos = -1;
        }

        if (edited)
        {
            // Record undo history. Coalesce a run of printable non-space chars into one step;
            // space/newline/delete/paste/any selection-replace start a new step.
            var typingChar = key == EditKey.None && text is not null && IsSingleCodePoint(text) && text[0] is not ('\n' or ' ') && !hasSel;
            if (!(typingChar && _typingGroup))
            {
                _undo.Add(new EditState(oValue, oCaret, oAnchor));
                if (_undo.Count > 300) _undo.RemoveAt(0);
                _redo.Clear();
            }
            _typingGroup = typingChar;
        }
        else _typingGroup = false;

        _caret = caret;
        _selAnchor = anchor;
        _caretMoved = true;
        if (edited)
        {
            _editBuffer = value;
            _listHi = -1; // the suggestion list re-filters on edit → drop the stale highlight
            // Live-commit only when the buffer is currently valid, so other bindings track it;
            // invalid text stays in the buffer (red border) and the model keeps its last good value.
            if (BufferValid(value)) BindingEngine.TrySet(_model, _focusKey, value);
            Refresh(); // buffer changed → rebuild so the field re-renders the new text
        }
        // A caret/selection-only change needs just a repaint (the caret + selection are drawn from
        // _caret/_selAnchor at render time), so we skip the rebuild — the host repaints on `true`.
        return true;
    }

    // ---- text: clipboard + word/line boundaries (host provides the actual clipboard I/O) ----

    /// <summary>The currently selected text in the focused field, or null if nothing is selected.
    /// A masked field (data-mask, e.g. an un-revealed &lt;cupri-password&gt;) never yields its
    /// plaintext to the clipboard — copy/cut are disabled until the value is revealed.</summary>
    public string? CopySelection()
    {
        if (_focusKey is null || _editBuffer is null || _selAnchor == _caret || _focusMask) return null;
        int s = Math.Clamp(Math.Min(_selAnchor, _caret), 0, _editBuffer.Length);
        int e = Math.Clamp(Math.Max(_selAnchor, _caret), 0, _editBuffer.Length);
        return e > s ? _editBuffer[s..e] : null;
    }

    /// <summary>Copy the selection and delete it (cut). Returns the cut text, or null.</summary>
    public string? CutSelection() { var t = CutSelectionCore(); if (t is not null) _inputsVersion++; return t; }
    private string? CutSelectionCore()
    {
        var t = CopySelection();
        if (t is not null) DispatchKey(null, EditKey.Delete);
        return t;
    }

    // ---- right-click context menu (Cut/Copy/Paste/Select-all on text fields) -----------------

    /// <summary>Open a context menu at (x,y) if it's over a text field. Focuses the field (a fresh
    /// focus has no selection, so Cut/Copy start disabled; right-clicking the already-focused field
    /// keeps its selection). Returns true if a menu opened or an open one closed (→ repaint).</summary>
    public bool DispatchContextMenu(float x, float y) => Bump(DispatchContextMenuCore(Zc(x), Zc(y)));
    private bool DispatchContextMenuCore(float x, float y)
    {
        EnsureLaidOut();
        var hit = HitTesting.HitTest(_root, x, y);

        // Publish the target FIRST, before any branch below decides what kind of menu this is (or
        // that there is none). An app needs to know what was under the pointer whether the engine
        // goes on to open a custom region, an editing menu, or nothing at all.
        RaiseContextTarget(hit, x, y);

        // A <cupri-context-menu> region under the pointer opens its own menu at the pointer.
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element is { } hostEl && hostEl.HasAttribute("data-cupri-ctx-host") && HostIndex(hostEl) is >= 0 and var idx)
            {
                _ctxOpen = false; _ctxCustomIndex = idx; _ctxX = x; _ctxY = y;
                Refresh(); // the rebuild reveals the menu at (x,y)
                return true;
            }

        RenderNode? field = hit;
        while (field is not null && field.Element?.GetAttribute("role") is not ("textbox" or "spinbutton"))
            field = field.Parent;

        if (field is null) // not over an editable or a custom region → close any open menu, else nothing to do
        {
            if (!_ctxOpen && _ctxCustomIndex < 0) return false;
            _ctxOpen = false; _ctxCustomIndex = -1; Refresh(); return true;
        }

        // Focus the field (does nothing if already focused — so an existing selection is kept).
        UpdateFocus(field.Element);
        _kbIndex = IndexOfFocusable(hit!); // hit is non-null here (field is hit or an ancestor)
        _focusVisible = false;

        _ctxOpen = true; _ctxX = x; _ctxY = y;
        _ctxHasSelection = CopySelection() is not null;
        _ctxHasText = !string.IsNullOrEmpty(_editBuffer);
        Refresh(); // rebuild injects the menu subtree
        return true;
    }

    // Is this node inside the open context menu? (walk up to the data-ctx-menu container)
    private static bool InContextMenu(RenderNode? n)
    {
        for (; n is not null; n = n.Parent)
            if (n.Element?.HasAttribute("data-ctx-menu") == true) return true;
        return false;
    }

    // The command a clicked menu row carries (data-cupri-context), searching up from the hit node.
    private static Interaction.ContextCommand? ContextCommandOf(RenderNode? n)
    {
        for (; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("data-cupri-context") is { Length: > 0 } cmd)
                return cmd switch
                {
                    "cut" => Interaction.ContextCommand.Cut,
                    "copy" => Interaction.ContextCommand.Copy,
                    "paste" => Interaction.ContextCommand.Paste,
                    "selectall" => Interaction.ContextCommand.SelectAll,
                    _ => (Interaction.ContextCommand?)null,
                };
        return null;
    }

    // Inject the menu as a position:fixed overlay at the pointer. Self-styled (inline) so it never
    // depends on the app's stylesheet or which component library is registered.
    private void InjectContextMenu(IDocument dom)
    {
        if (dom.Body is not { } body) return;
        var x = _ctxX.ToString(CultureInfo.InvariantCulture);
        var y = _ctxY.ToString(CultureInfo.InvariantCulture);

        static string Row(string cmd, string label, string hint, bool enabled)
        {
            var color = enabled ? "#1e2430" : "#b0b6c0";
            var attr = enabled ? $" role='menuitem' data-cupri-context='{cmd}'" : "";
            return $"<div{attr} style='display:flex;justify-content:space-between;gap:28px;"
                 + $"padding:7px 12px;border-radius:6px;color:{color};font-size:14px;'>"
                 + $"<span>{label}</span><span style='color:#9aa2b1'>{hint}</span></div>";
        }
        const string sep = "<div style='height:1px;background:#e6e9f0;margin:4px 6px;'></div>";

        var menu = $"<div data-ctx-menu data-ctx-clamp style='position:fixed;left:{x}px;top:{y}px;"
            + "background:#ffffff;border:1px #e6e9f0;border-radius:8px;padding:5px;min-width:180px;"
            + "font-family:sans-serif;z-index:60;'>"
            + Row("cut", "Cut", "Ctrl+X", _ctxHasSelection)
            + Row("copy", "Copy", "Ctrl+C", _ctxHasSelection)
            + Row("paste", "Paste", "Ctrl+V", true)
            + sep
            + Row("selectall", "Select All", "Ctrl+A", _ctxHasText)
            + "</div>";

        body.Insert(AngleSharp.Dom.AdjacentPosition.BeforeEnd, menu);
    }

    // ---- custom <cupri-context-menu> --------------------------------------------------------------

    // Document-order index of a context-menu host, so the open one can be re-found after a rebuild.
    private int HostIndex(IElement host)
    {
        if (_dom is null) return -1;
        var hosts = _dom.QuerySelectorAll("[data-cupri-ctx-host]");
        for (var i = 0; i < hosts.Length; i++) if (ReferenceEquals(hosts[i], host)) return i;
        return -1;
    }

    // Reveal the open custom menu at the pointer: flip its popup to display:block, place it at (x,y),
    // and mark it data-ctx-clamp so layout keeps it on-screen (same clamp the text menu uses).
    private void RevealCustomContextMenu(IDocument dom)
    {
        var hosts = dom.QuerySelectorAll("[data-cupri-ctx-host]");
        if (_ctxCustomIndex >= hosts.Length) { _ctxCustomIndex = -1; return; } // host went away since the click
        if (hosts[_ctxCustomIndex].QuerySelector(".cupri-ctx-menu") is not { } menu) { _ctxCustomIndex = -1; return; }
        var x = _ctxX.ToString(CultureInfo.InvariantCulture);
        var y = _ctxY.ToString(CultureInfo.InvariantCulture);
        menu.SetAttribute("data-ctx-clamp", "");
        menu.SetAttribute("style", $"display:block;left:{x}px;top:{y}px;"); // position:fixed + look come from .cupri-ctx-menu
    }

    private static bool InCustomContextMenu(RenderNode? n)
    {
        for (; n is not null; n = n.Parent) if (n.Element?.HasAttribute("data-cupri-ctx-menu") == true) return true;
        return false;
    }

    // The pointer is over an actionable (leaf) row — not a submenu parent or the popup's own padding.
    private static bool OnLeafMenuItem(RenderNode? n)
    {
        for (; n is not null; n = n.Parent)
            if (n.Element?.ClassList.Contains("cupri-menu-item") == true)
                return !n.Element.ClassList.Contains("cupri-menu-parent");
        return false;
    }

    // ---- toast stack (doc.Toast) ------------------------------------------------------------------

    // Inject the current toasts at the body end. Entering/Leaving render off-screen (translated + faded);
    // the .cupri-toast-item transition animates the flip to/from the shown state as Animate advances phases.
    private void InjectToaster(IDocument dom)
    {
        if (dom.Body is not { } body || _toasts.Count == 0) return;
        var sb = new System.Text.StringBuilder("<div class='cupri-toaster'>");
        foreach (var t in _toasts)
        {
            var off = t.Phase != ToastPhase.Shown; // Entering + Leaving sit off to the right, transparent
            var kind = t.Kind.Length > 0 ? " " + t.Kind : "";
            sb.Append($"<div class='cupri-toast-item{kind}' role='status' style='opacity:{(off ? "0" : "1")};")
              .Append($"transform:translateX({(off ? "120%" : "0")})'>")
              .Append(EscapeHtml(t.Msg)).Append("</div>");
        }
        sb.Append("</div>");
        body.Insert(AngleSharp.Dom.AdjacentPosition.BeforeEnd, sb.ToString());
    }

    // Advance each toast: Entering → Shown (flip in) on its first tick; Shown → Leaving (flip out) once it
    // has sat for ToastShowSeconds; Leaving → removed once the exit slide has run. Rebuilds on any change so
    // the transition engine sees the new target. Returns true while any toast remains (keeps the loop alive).
    private bool StepToasts(double t)
    {
        var changed = false;
        for (var i = _toasts.Count - 1; i >= 0; i--)
        {
            var e = _toasts[i];
            switch (e.Phase)
            {
                case ToastPhase.Entering: e.Phase = ToastPhase.Shown; e.T = t; changed = true; break;
                case ToastPhase.Shown when t - e.T >= ToastShowSeconds: e.Phase = ToastPhase.Leaving; e.T = t; changed = true; break;
                case ToastPhase.Leaving when t - e.T >= ToastExitSeconds: _toasts.RemoveAt(i); changed = true; break;
            }
        }
        if (changed) Rebuild();
        return _toasts.Count > 0;
    }

    private static string EscapeHtml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Undo the last edit in the focused field (Ctrl+Z). Returns true if it changed anything.</summary>
    public bool Undo() => Bump(UndoCore());
    private bool UndoCore()
    {
        if (_focusKey is null || _undo.Count == 0) return false;
        _redo.Add(new EditState(_editBuffer ?? "", _caret, _selAnchor));
        var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        ApplyEditState(s);
        return true;
    }

    /// <summary>Redo the last undone edit (Ctrl+Y / Ctrl+Shift+Z). Returns true if it changed anything.</summary>
    public bool Redo() => Bump(RedoCore());
    private bool RedoCore()
    {
        if (_focusKey is null || _redo.Count == 0) return false;
        _undo.Add(new EditState(_editBuffer ?? "", _caret, _selAnchor));
        var s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        ApplyEditState(s);
        return true;
    }

    private void ApplyEditState(EditState s)
    {
        _editBuffer = s.Buffer;
        _caret = Math.Clamp(s.Caret, 0, s.Buffer.Length);
        _selAnchor = Math.Clamp(s.Anchor, 0, s.Buffer.Length);
        _caretMoved = true;
        _typingGroup = false;
        if (_model is not null && _focusKey is not null && BufferValid(s.Buffer))
            BindingEngine.TrySet(_model, _focusKey, s.Buffer);
        Refresh();
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int WordLeft(string s, int i)
    {
        i = Math.Clamp(i, 0, s.Length);
        while (i > 0 && !IsWordChar(s[i - 1])) i--;
        while (i > 0 && IsWordChar(s[i - 1])) i--;
        return i;
    }

    private static int WordRight(string s, int i)
    {
        i = Math.Clamp(i, 0, s.Length);
        while (i < s.Length && !IsWordChar(s[i])) i++;
        while (i < s.Length && IsWordChar(s[i])) i++;
        return i;
    }

    // The word (or whitespace/punctuation run) around index i — for double-click selection.
    private static (int, int) WordAt(string s, int i)
    {
        if (s.Length == 0) return (0, 0);
        i = Math.Clamp(i, 0, s.Length);
        int a = i, b = i;
        if ((i < s.Length && IsWordChar(s[i])) || (i > 0 && IsWordChar(s[i - 1])))
        {
            while (a > 0 && IsWordChar(s[a - 1])) a--;
            while (b < s.Length && IsWordChar(s[b])) b++;
        }
        else
        {
            while (a > 0 && !IsWordChar(s[a - 1]) && s[a - 1] != '\n') a--;
            while (b < s.Length && !IsWordChar(s[b]) && s[b] != '\n') b++;
        }
        return (a, b);
    }

    // The whole line around index i (between newlines) — for triple-click selection.
    private static (int, int) LineAt(string s, int i)
    {
        i = Math.Clamp(i, 0, s.Length);
        int a = i, b = i;
        while (a > 0 && s[a - 1] != '\n') a--;
        while (b < s.Length && s[b] != '\n') b++;
        return (a, b);
    }

    // Toggle/select a checkbox, switch, or radio from its element alone (no RenderNode needed) —
    // shared by a direct click on the control and a click on its adjacent text label.
    private bool ActivateControl(IElement el) => el.GetAttribute("role") switch
    {
        "switch" or "checkbox" => ToggleSwitch(el),
        "radio" => el.GetAttribute("data-bind-group") is { Length: > 0 } gp && _model is not null
            ? BindingEngine.TrySet(_model, gp, el.GetAttribute("value"))
            : SetChecked(el, true),
        _ => false,
    };

    private static bool IsLabelableControl(IElement? el) =>
        el?.GetAttribute("role") is "switch" or "checkbox" or "radio";

    /// <summary>
    /// The controls a click at <paramref name="hit"/> could activate as a label, nearest first.
    /// Labels are authored as siblings of the control, so we look outward from the clicked node: the
    /// immediately-adjacent control, preferring the one BEFORE the label (the "[box] Label" pattern)
    /// then the one AFTER it (the "Label [switch]" pattern). A following control is only offered when
    /// it has no trailing label of its own, so a group heading like "Size" that precedes the first
    /// radio doesn't hijack it. (The whole ancestor chain of <paramref name="hit"/> is non-interactive
    /// when this runs — ActivateFrom already found no control above it — so walking out to adjacent
    /// siblings stays on inert text.) Enumerating candidates rather than acting lets <see cref="CursorAt"/>
    /// and <see cref="ActivateLabel"/> share ONE definition of "this text is a label", so the cursor
    /// can't promise a click the activation wouldn't honour.
    /// </summary>
    private static IEnumerable<IElement> LabelTargets(RenderNode hit)
    {
        RenderNode? node = hit;
        for (var hops = 0; node is not null && hops < 5; node = node.Parent, hops++)
        {
            if (node.Element is not { } el) continue;
            // An adjacent control is only a label target when the click came through inert text.
            // A spinbutton/textbox/slider/button is an interaction boundary even when its particular
            // click was not consumed (for example, clicking the editable body of cupri-number). Without
            // this boundary, a number field beside a switch is mistaken for that switch's label.
            if (IsFocusableRole(el)) yield break;
            if (el.PreviousElementSibling is { } prev && IsLabelableControl(prev)) yield return prev;
            if (el.NextElementSibling is { } next && IsLabelableControl(next) && !HasTrailingLabel(next))
                yield return next;
        }
    }

    /// <summary>
    /// A click that hit no control but landed on a checkbox/radio/switch's text label activates
    /// that control (HTML <c>&lt;label&gt;</c> behaviour). Returns the activated control, or null —
    /// a candidate that declines to act (e.g. an unbound radio) falls through to the next.
    /// </summary>
    private IElement? ActivateLabel(RenderNode hit)
    {
        foreach (var control in LabelTargets(hit))
            if (ActivateControl(control)) return control;
        return null;
    }

    // True when a control already owns a text label on its far side — i.e. its next sibling is a
    // non-control element — so an element before it is a heading, not its label.
    private static bool HasTrailingLabel(IElement control) =>
        control.NextElementSibling is { } sib && !IsLabelableControl(sib);

    private bool ToggleSwitch(IElement el)
    {
        var path = el.GetAttribute("data-bind-checked");
        if (path is null || _model is null) return false;
        var current = BindingEngine.Resolve(_model, path) as bool? ?? false;
        return BindingEngine.TrySet(_model, path, !current);
    }

    private bool SetChecked(IElement el, bool value)
    {
        var path = el.GetAttribute("data-bind-checked");
        return path is not null && _model is not null && BindingEngine.TrySet(_model, path, value);
    }

    // Walk up to the nearest element with a two-way-bound `open` and set/toggle it.
    private bool SetNearestOpen(RenderNode node, bool value)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("data-bind-open") is { Length: > 0 } path && _model is not null)
                return BindingEngine.TrySet(_model, path, value);
        return false;
    }

    // Click-away dismissal: close any open anchored popup (select / date / time picker / popover) whose
    // open state is a bound flag, when the click lands outside both the popup and its trigger. Mutates
    // the flag like SetNearestOpen but does NOT consume the click, so it composes with whatever else the
    // click does (e.g. opening a different picker). A rebuild by the caller applies it.
    private bool CloseStrayPopups(RenderNode? hit)
    {
        if (_dom is null || _model is null) return false;
        var closed = false;
        foreach (var popup in _dom.QuerySelectorAll("[data-cupri-anchor]"))
        {
            var anchorId = popup.GetAttribute("data-cupri-anchor");
            var insideOrTrigger = false;
            for (var n = hit; n is not null; n = n.Parent)
                if (n.Element is { } el && (ReferenceEquals(el, popup) || el.GetAttribute("id") == anchorId)) { insideOrTrigger = true; break; }
            if (insideOrTrigger) continue;

            for (var e = popup; e is not null; e = e.ParentElement)
                if (e.GetAttribute("data-bind-open") is { Length: > 0 } path)
                { if (BindingEngine.TrySet(_model, path, false)) closed = true; break; }
        }
        return closed;
    }

    private bool ToggleNearestOpen(RenderNode node)
    {
        for (var n = node; n is not null; n = n.Parent)
            if (n.Element?.GetAttribute("data-bind-open") is { Length: > 0 } path && _model is not null)
            {
                var current = BindingEngine.Resolve(_model, path) as bool? ?? false;
                return BindingEngine.TrySet(_model, path, !current);
            }
        return false;
    }

    // A stepper (+/-) inside a number field: nudge the field's bound value by step*delta, clamped.
    private bool StepNumber(RenderNode node, string stepRaw)
    {
        if (_model is null || !int.TryParse(stepRaw, out var dir)) return false;
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n.Element?.GetAttribute("data-bind-value") is not { Length: > 0 } path) continue;
            if (_focusKey == path) CommitBuffer(); // flush any in-progress edit before stepping
            var cur = BindingEngine.Resolve(_model, path) is { } v && double.TryParse(v.ToString(), out var d) ? d : 0;
            var step = double.TryParse(n.Element.GetAttribute("data-step"), out var s) ? s : 1;
            var next = cur + dir * step;
            if (double.TryParse(n.Element.GetAttribute("data-min"), out var min)) next = Math.Max(min, next);
            if (double.TryParse(n.Element.GetAttribute("data-max"), out var max)) next = Math.Min(max, next);
            // Keep integers integral so a bound int round-trips cleanly.
            var text = next == Math.Floor(next) ? ((long)next).ToString() : next.ToString();
            var ok = BindingEngine.TrySet(_model, path, text);
            if (_focusKey == path) { _editBuffer = text; _caret = text.Length; } // keep the buffer in sync with the step
            return ok;
        }
        return false;
    }

    // ---- field validation: permissive typing, validate on commit (blur/Enter) --------------
    // The philosophy across all fields: let the user type freely (invalid states show a red
    // border), and only validate/clamp when the field loses focus — so text stays editable.

    // Is the buffer currently valid for its field? Plain text: always. Numeric: parseable + in range.
    private bool BufferValid(string buf)
    {
        if (_focusNumeric)
        {
            if (!double.TryParse(buf, out var n)) return false;
            if ((_focusMin is { } mn && n < mn) || (_focusMax is { } mx && n > mx)) return false;
        }
        // required / pattern / minlength drive the red border mid-edit too, not just numeric range.
        return FieldValidation.Check(_focusRequired, _focusPattern, _focusMinLen, null, null, buf, null) is null;
    }

    // The value to write on commit, or null to leave the model unchanged (revert an unparseable buffer).
    private string? BufferCommit(string buf)
    {
        if (!_focusNumeric) return buf;
        if (!double.TryParse(buf, out var n)) return null; // e.g. "" or "abc" — keep the last good value
        if (_focusMin is { } mn) n = Math.Max(mn, n);
        if (_focusMax is { } mx) n = Math.Min(mx, n);
        return n == Math.Floor(n) ? ((long)n).ToString() : n.ToString();
    }

    // Validate and commit the current edit buffer to the model, then clear it (blur). Marks the field
    // "touched" so its inline error can show now that the user has left it.
    /// <summary>Append one entry to a comma-separated list at <paramref name="path"/>, refusing a
    /// duplicate. A comma-joined string is the list shape because it round-trips through the same
    /// binding a text field uses — a real collection would need the binder to write back into an
    /// element of it, which is a bigger change than a tag box is worth.</summary>
    private bool AppendTag(string path, string entry)
    {
        if (_model is null) return false;
        entry = entry.Replace(",", " ").Trim();          // a comma inside an entry would split it in two
        if (entry.Length == 0) return false;

        var current = BindingEngine.Resolve(_model, path)?.ToString() ?? "";
        foreach (var existing in current.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(existing.Trim(), entry, StringComparison.OrdinalIgnoreCase))
                return false;                            // already there: adding it twice is never meant

        return BindingEngine.TrySet(_model, path, current.Trim().Length == 0 ? entry : current + "," + entry);
    }

    /// <summary>Drop the last entry from a comma-separated list. Returns false when there is nothing
    /// to drop, so an empty tag box swallows nothing and Backspace stays inert rather than appearing
    /// to do something.</summary>
    private bool RemoveLastTag(string path)
    {
        if (_model is null) return false;
        var parts = (BindingEngine.Resolve(_model, path)?.ToString() ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return false;
        parts.RemoveAt(parts.Count - 1);
        return BindingEngine.TrySet(_model, path, string.Join(",", parts));
    }

    private void CommitBuffer()
    {
        if (_focusKey is not null && _editBuffer is not null && _model is not null
            && BufferCommit(_editBuffer) is { } committed)
            BindingEngine.TrySet(_model, _focusKey, committed);
        if (_focusKey is not null) _touched.Add(_focusKey);
        _editBuffer = null;
    }

    // Inline validation: for each bound field with rules that is invalid AND has been visited (or the form
    // was submitted), flag it invalid and inject an error message after it. The focused field is left to
    // its mid-edit red border (no message while you're still typing in it).
    /// <summary>Is this field inside the named <c>&lt;cupri-form&gt;</c>? Walked rather than selected,
    /// because the caller already has the element and a selector per field would re-query the DOM.</summary>
    private static bool InForm(IElement field, string formName)
    {
        for (IElement? e = field; e is not null; e = e.ParentElement)
            if (e.GetAttribute("data-cupri-form") == formName) return true;
        return false;
    }

    private void ApplyValidation(IDocument dom)
    {
        foreach (var field in dom.QuerySelectorAll("[data-bind-value]"))
        {
            if (!FieldValidation.HasRules(field)) continue;
            var key = field.GetAttribute("data-bind-value")!;
            if (key == _focusKey) continue;
            // A field is revealed because it was visited, or because a submit asked for every error.
            // A SCOPED submit asks only for its own form's: without this a Validate("login") would
            // still light up the signup box beside it, which is the thing scoping exists to stop.
            var bySubmit = _validateAll && (_validateScope is null || InForm(field, _validateScope));
            if (!bySubmit && !_touched.Contains(key)) continue;
            if (FieldValidation.Evaluate(field, field.GetAttribute("value") ?? "") is not { } error) continue;

            field.SetAttribute("data-invalid", "");
            var msg = dom.CreateElement("div");
            msg.ClassName = "cupri-field-error";
            msg.TextContent = error;
            field.Parent?.InsertBefore(msg, field.NextSibling);
        }
    }

    /// <summary>Reveal every validated field's inline error (mark all fields touched) and re-render — call
    /// from a form's submit handler. Returns true when every field is currently valid.</summary>
    public bool ValidateAll() => ValidateScope(null);

    /// <summary>Validate only the fields inside one <c>&lt;cupri-form name="…"&gt;</c>, rather than
    /// every bound field on the page.
    ///
    /// <para>A page with two independent forms could not be validated a form at a time:
    /// <see cref="ValidateAll"/> is document-wide, so submitting the login box reported the
    /// half-finished signup box's errors as well — and showed them, since validation reveals every
    /// field it checks. Naming the form is how you say which one you meant.</para>
    ///
    /// <para>An unknown name validates nothing and returns true, which is the same answer a form with
    /// no rules gives.</para></summary>
    public bool Validate(string formName) => ValidateScope(formName);

    private bool ValidateScope(string? formName)
    {
        _validateScope = formName;
        _validateAll = true;
        Rebuild();
        if (_dom is null) return true;
        var fields = formName is null
            ? _dom.QuerySelectorAll("[data-bind-value]")
            : _dom.QuerySelectorAll($"[data-cupri-form=\"{formName}\"] [data-bind-value]");
        foreach (var field in fields)
            if (FieldValidation.HasRules(field) && FieldValidation.Evaluate(field, field.GetAttribute("value") ?? "") is not null)
                return false;
        return true;
    }

    // Which form's fields the pending "show every error" pass applies to (null = the whole document).
    // Without this a scoped Validate would still light up every field on the page, because the
    // message-injection pass below is what actually reveals them.
    private string? _validateScope;

    // The player a video-seek slider controls: the element marks itself data-video-role='seek'
    // and its enclosing <cupri-video> names the src. Null when it isn't a seek bar (or the video
    // has no player — no backend — in which case the bar is marked disabled anyway).
    private Media.IVideoPlayer? SeekPlayerOf(IElement el)
    {
        if (el.GetAttribute("data-video-role") != "seek" || el.GetAttribute("aria-disabled") == "true") return null;
        for (var e = (IElement?)el; e is not null; e = e.ParentElement)
            if (e.GetAttribute("data-cupri-video") is { Length: > 0 } src && _videoPlayers.TryGetValue(src, out var p))
                return p;
        return null;
    }

    // Begin a slider interaction: cache its geometry so drag-moves don't need the node.
    private bool StartSliderDrag(RenderNode node, IElement el, float x)
    {
        // The video seek bar: same geometry mapping, but the value goes to the PLAYER (there is
        // no model path). Scrubbing works because Position-setting presents the landing frame.
        if (SeekPlayerOf(el) is { } seekPlayer)
        {
            var sBox = HitTesting.ScreenBox(node);
            _dragging = true;
            _dragX0 = sBox.X;
            _dragPad = node.ContentLeftInset;
            _dragInnerW = sBox.W - node.HorizontalInsets;
            _dragMin = 0;
            _dragMax = Math.Max(seekPlayer.Duration, 0.01);
            _dragPath = null;
            _dragClampMin = _dragClampMax = null;
            _dragSeek = seekPlayer;
            return ApplySliderValue(x);
        }

        var path = el.GetAttribute("data-bind-value");
        if (path is null || _model is null) return false;

        // A thumb may take its geometry from an ancestor TRACK instead of itself. A range slider has
        // two thumbs sharing one track, so each is its own role=slider — separately focusable, with
        // its own aria-valuenow, and nudged by the existing arrow handling — but mapping the pointer
        // across an 18px thumb would make it undraggable. The track is the scale; the thumb is just
        // the grab handle.
        var geomNode = node;
        for (var p = node; p is not null; p = p.Parent)
            if (p.Element?.HasAttribute("data-slider-track") == true) { geomNode = p; break; }

        // Box AND insets both come from whichever node supplies the scale — mixing the track's box
        // with the thumb's padding would offset every value by the thumb's own inset.
        var box = HitTesting.AbsoluteBox(geomNode);
        _dragging = true;
        _dragX0 = box.X;
        _dragPad = geomNode.ContentLeftInset;
        _dragInnerW = box.W - geomNode.HorizontalInsets;
        // min/max are the SCALE — what the track's width represents. They must stay the full range
        // even for a thumb that cannot reach all of it, or the pointer stops landing where you point:
        // scaling a range thumb across only its permitted span put a drag to 60% of the track at 60%
        // of what was left, which is a different number and visibly wrong under the cursor.
        _dragMin = ParseAttr(el, "min", 0);
        _dragMax = ParseAttr(el, "max", 100);
        // …and the LIMIT is separate, for a thumb bounded by something other than the scale — the
        // other thumb of a range. Absent on an ordinary slider, where the scale is the limit.
        _dragClampMin = el.HasAttribute("data-clamp-min") ? ParseAttr(el, "data-clamp-min", _dragMin) : null;
        _dragClampMax = el.HasAttribute("data-clamp-max") ? ParseAttr(el, "data-clamp-max", _dragMax) : null;
        _dragPath = path;
        _dragSeek = null;
        _dragUndecided = false;

        // BOTH THUMBS ON THE SAME VALUE. Only the one painted last is hit-testable there, so every
        // press would grab that same thumb and the other could never be moved again — a range dragged
        // shut is a range that stays shut. Which thumb was meant is genuinely unknowable at press
        // time, so the choice waits for the first move: pull left and you meant the low thumb, pull
        // right and you meant the high one. Nothing is written until then, so a press that never moves
        // changes nothing.
        //
        // (The alternative — let them cross and swap which is which — reaches the same end state, but
        // swaps the labels under the finger mid-drag and gives up the no-crossing guarantee for every
        // other drag as well. Deciding by direction costs one deferred frame and changes nothing else.)
        var side = el.GetAttribute("data-drag-side");
        if (el.GetAttribute("data-peer-path") is { Length: > 0 } peerPath
            && (side == "low" || side == "high")
            && BindingEngine.Resolve(_model, peerPath) is { } peerRaw
            && double.TryParse(peerRaw.ToString(), System.Globalization.NumberStyles.Any,
                               CultureInfo.InvariantCulture, out var peerValue))
        {
            var mine = ParseAttr(el, "aria-valuenow", double.NaN);
            if (!double.IsNaN(mine) && Math.Abs(mine - peerValue) < 0.5)
            {
                _dragUndecided = true;
                _dragPressX = x;
                _dragSharedValue = mine;
                _dragLowPath = side == "low" ? path : peerPath;
                _dragHighPath = side == "low" ? peerPath : path;
                _dragPath = null;                    // decided on the first move, not now
                return true;
            }
        }
        return ApplySliderValue(x);
    }

    // A press that landed where both range thumbs sit. Which one is being dragged is decided by the
    // direction of the first move, so these hold the candidates until then.
    private bool _dragUndecided;
    private string? _dragLowPath, _dragHighPath;
    private float _dragPressX;
    private double _dragSharedValue;

    /// <summary>Resolve a deferred range drag once the pointer has committed to a direction. Returns
    /// false while the movement is still too small to read as one, which leaves both thumbs alone —
    /// the alternative is guessing on the first stray pixel and moving the wrong one.</summary>
    private bool DecideRangeThumb(float x)
    {
        const float threshold = 2f;               // below this a "drag" is hand tremor, not intent
        var dx = x - _dragPressX;
        if (MathF.Abs(dx) < threshold) return false;

        var goingLeft = dx < 0;
        _dragPath = goingLeft ? _dragLowPath : _dragHighPath;
        // The thumb left behind is the bound: neither may pass the point they were sharing.
        _dragClampMin = goingLeft ? null : _dragSharedValue;
        _dragClampMax = goingLeft ? _dragSharedValue : null;
        _dragUndecided = false;
        return _dragPath is not null;
    }

    private Media.IVideoPlayer? _dragSeek; // the seek bar's player while a scrub drag is live
    // A bound the dragged thumb may not pass, distinct from the scale it is measured on.
    private double? _dragClampMin, _dragClampMax;

    private bool ApplySliderValue(float x)
    {
        // A deferred range drag decides which thumb it is on the first real movement.
        if (_dragUndecided && !DecideRangeThumb(x)) return false;

        var ratio = _dragInnerW > 0 ? Math.Clamp((x - _dragX0 - _dragPad) / _dragInnerW, 0, 1) : 0;
        var value = _dragMin + ratio * (_dragMax - _dragMin);
        // A thumb may be limited by something other than the scale — the other thumb of a range.
        if (_dragClampMin is { } lo) value = Math.Max(value, lo);
        if (_dragClampMax is { } hi) value = Math.Min(value, hi);
        if (_dragSeek is { } seek)
        {
            seek.Position = value;   // unrounded: sub-second scrubbing
            Refresh();               // clocks + fill track the thumb while dragging
            return true;
        }
        if (_dragPath is null || _model is null) return false;
        return BindingEngine.TrySet(_model, _dragPath, Math.Round(value));
    }

    /// <summary>Pointer move: drag a slider, or update :hover. Returns true if a repaint is needed.</summary>
    // Grab a card by its handle: record the source column, every column in its board group (all
    // .cupri-reorder lists under a shared .cupri-board — else just this list), each column's cards and
    // their mid-lines + X-span for hit-testing which column/slot the pointer is over as it's dragged.
    private bool StartReorder(RenderNode handle, float x, float y)
    {
        RenderNode? item = handle;
        while (item is not null && item.Element?.ClassList.Contains("cupri-reorder-item") != true) item = item.Parent;
        if (item?.Parent is not { } list) return false;

        RenderNode? board = list;
        while (board is not null && board.Element?.ClassList.Contains("cupri-board") != true) board = board.Parent;
        var lists = new List<RenderNode>();
        if (board is not null) CollectReorderLists(board, lists); else lists.Add(list);

        var cols = new List<ReorderCol>();
        var all = new List<RenderNode>();
        int fromCol = -1, from = -1;
        for (var ci = 0; ci < lists.Count; ci++)
        {
            var items = new List<RenderNode>();
            foreach (var c in lists[ci].Children) if (c.Element?.ClassList.Contains("cupri-reorder-item") == true) items.Add(c);
            // ScreenBox, not AbsoluteBox: the pointer these mids are compared against is in SCREEN
            // space. Inside a scrolled page the unscrolled boxes sit below where the pointer can
            // reach — the row lifted and followed, but the target slot never changed, so the drop
            // was a silent no-op (the field report: "drag row doesn't work").
            var mids = new float[items.Count];
            for (var i = 0; i < items.Count; i++) { var b = HitTesting.ScreenBox(items[i]); mids[i] = b.Y + b.H / 2f; }
            var lb = HitTesting.ScreenBox(lists[ci]);
            cols.Add(new ReorderCol(lists[ci], items, mids, lb.X, lb.X + lb.W));
            all.AddRange(items);
            var idx = items.IndexOf(item);
            if (idx >= 0) { fromCol = ci; from = idx; }
        }
        if (fromCol < 0) return false;

        _reorderCols = cols; _reorderItems = all; _reorderList = list; _reorderCard = item;
        _reorderFromCol = _reorderToCol = fromCol; _reorderFrom = _reorderTo = from;
        _reorderX0 = x; _reorderY0 = y;
        // How far the other cards slide to open/close the gap = the dragged card's own footprint (its
        // height + the list gap), NOT the distance between the first two cards — which overshoots when a
        // card wraps to a taller height, sliding the rest off the top of the column and leaving a gap below.
        var srcCol = cols[fromCol];
        var gap = srcCol.Items.Count >= 2
            ? MathF.Max(0f, (srcCol.Mids[1] - srcCol.Mids[0]) - (srcCol.Items[0].Height + srcCol.Items[1].Height) / 2f)
            : 8f;
        _reorderShift = item.Height + gap;
        _reorderAnimT = double.NaN;
        item.Dragging = true;
        return true;
    }

    // Every .cupri-reorder list under a board (a found list is a column — don't descend into its cards).
    private static void CollectReorderLists(RenderNode n, List<RenderNode> outp)
    {
        foreach (var c in n.Children)
            if (c.Element?.ClassList.Contains("cupri-reorder") == true) outp.Add(c);
            else CollectReorderLists(c, outp);
    }

    // Dragging: the lifted card follows the pointer (both axes, so it can cross columns); the column under
    // the pointer opens a gap at the drop slot and the source column closes the one the card left. Paint-time.
    private bool MoveReorder(float x, float y)
    {
        if (_reorderCols is not { } cols || _reorderCard is not { } card) return false;
        card.DragOffsetX = x - _reorderX0;
        card.DragOffsetY = y - _reorderY0;

        // Target column: the one whose X-span holds the pointer, else the nearest by centre.
        var toCol = _reorderFromCol; var best = float.MaxValue;
        for (var ci = 0; ci < cols.Count; ci++)
        {
            if (x >= cols[ci].Left && x < cols[ci].Right) { toCol = ci; best = -1; break; }
            var d = MathF.Abs(x - (cols[ci].Left + cols[ci].Right) / 2f);
            if (best >= 0 && d < best) { best = d; toCol = ci; }
        }

        // Target slot in that column, from the pointer's Y against the column's card mid-lines.
        var mids = cols[toCol].Mids;
        int to;
        if (toCol == _reorderFromCol)
        {
            to = _reorderFrom;                                                   // within the source column
            for (var i = _reorderFrom + 1; i < mids.Length && y > mids[i]; i++) to = i;
            for (var i = _reorderFrom - 1; i >= 0 && y < mids[i]; i--) to = i;
        }
        else { to = 0; for (var i = 0; i < mids.Length; i++) if (y > mids[i]) to = i + 1; } // into another column
        _reorderToCol = toCol; _reorderTo = to;

        ApplyReorderGaps();
        return true;
    }

    // Slide each card toward its gap target: within the source column the cards between the source and target
    // slots shift one place; across columns the source closes (cards after the origin move up) and the target
    // opens (cards at/after the drop move down). The lifted card is skipped — it tracks the pointer.
    private void ApplyReorderGaps()
    {
        if (_reorderCols is not { } cols) return;
        for (var ci = 0; ci < cols.Count; ci++)
        {
            var items = cols[ci].Items;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Dragging) continue;
                float t;
                if (ci == _reorderFromCol && ci == _reorderToCol)
                    t = _reorderTo > _reorderFrom && i > _reorderFrom && i <= _reorderTo ? -_reorderShift
                      : _reorderTo < _reorderFrom && i >= _reorderTo && i < _reorderFrom ? _reorderShift : 0f;
                else if (ci == _reorderFromCol) t = i > _reorderFrom ? -_reorderShift : 0f;   // source closes
                else if (ci == _reorderToCol)   t = i >= _reorderTo ? _reorderShift : 0f;      // target opens
                else t = 0f;
                items[i].DragTargetY = t;
            }
        }
    }

    // Ease each shifting row toward its target offset (the lifted row is skipped — it tracks the pointer).
    // Returns true while any row is still moving, which keeps the host's frame loop ticking mid-drag.
    private bool EaseReorder(double now)
    {
        if (_reorderItems is not { } items) return false;
        var dt = double.IsNaN(_reorderAnimT) ? 0 : Math.Clamp(now - _reorderAnimT, 0, 0.1);
        _reorderAnimT = now;
        var f = (float)(1 - Math.Exp(-dt * 18)); // frame-rate-independent smoothing
        var moving = false;
        foreach (var it in items)
        {
            if (it.Dragging) continue;
            var diff = it.DragTargetY - it.DragOffsetY;
            if (MathF.Abs(diff) < 0.4f) { it.DragOffsetY = it.DragTargetY; continue; }
            it.DragOffsetY += diff * f;
            moving = true;
        }
        return moving;
    }

    /// <summary>True while a reorder drag's rows are still sliding toward their gap — keeps the host
    /// rendering between pointer moves so the slide eases instead of snapping.</summary>
    public bool ReorderEasing
    {
        get
        {
            if (_reorderItems is not { } items) return false;
            foreach (var it in items) if (!it.Dragging && MathF.Abs(it.DragTargetY - it.DragOffsetY) > 0.4f) return true;
            return false;
        }
    }

    // A rebuild replaces every RenderNode, so any node the pointer was mid-drag on is now orphaned. The
    // paint-time drags (reorder / split / resize / scrollbar) hold references to those dead nodes — drop
    // them, or pointer-moves keep routing to a drag that no longer paints (the reorder row looks stuck or
    // stacked on its neighbours and :hover stops updating) until some unrelated event clears it. This is
    // the safety net for a missed pointer-up (released off-window) or a rebuild landing mid-drag. Any
    // committed size/ratio was already carried across by CaptureScroll, so nothing visible is lost. The
    // slider drag (_dragging) is deliberately NOT reset: it rebuilds on every move by design and re-finds
    // its node, so it must survive a rebuild.
    private void CancelOrphanedPointerDrags()
    {
        _reorderList = null; _reorderItems = null; _reorderCols = null; _reorderCard = null;
        _splitA = _splitB = null;
        _resizeDrag = null;
        _scrollDrag = null;
    }

    // Drop: clear the offsets and, if the card landed in a new column/slot, fire the reorder event (which
    // moves it in the model and rebuilds). A no-op drop just repaints the offsets away.
    private void EndReorder()
    {
        if (_reorderItems is { } items)
            foreach (var it in items) { it.DragOffsetX = 0f; it.DragOffsetY = 0f; it.DragTargetY = 0f; it.Dragging = false; }
        var cols = _reorderCols;
        var (fromCol, from, toCol, to) = (_reorderFromCol, _reorderFrom, _reorderToCol, _reorderTo);
        _reorderList = null; _reorderItems = null; _reorderCols = null; _reorderCard = null;
        if (cols is null) return;
        var fromEl = cols[fromCol].List.Element;
        var toEl = cols[toCol].List.Element;
        if ((toCol != fromCol || to != from) && fromEl is not null && toEl is not null)
        { _onReorder?.Invoke(new ReorderEvent(fromEl, from, to, toEl)); Refresh(); }
    }

    // Keyboard reorder: move the focused row's item one slot (↑ up / ↓ down), commit via OnReorder, and
    // keep focus on the moved row so repeated presses keep moving it — the keyboard equivalent of the drag.
    private bool KeyboardReorder(RenderNode handle, int dir)
    {
        RenderNode? item = handle;
        while (item is not null && item.Element?.ClassList.Contains("cupri-reorder-item") != true) item = item.Parent;
        if (item?.Parent is not { } list || list.Element is not { } listEl) return false;

        var items = new List<RenderNode>();
        foreach (var c in list.Children) if (c.Element?.ClassList.Contains("cupri-reorder-item") == true) items.Add(c);
        var from = items.IndexOf(item);
        var to = Math.Clamp(from + dir, 0, items.Count - 1);
        if (from < 0 || to == from) return true; // already at an edge — consume the key

        _onReorder?.Invoke(new ReorderEvent(listEl, from, to, listEl)); // keyboard reorder stays within one list
        Refresh();
        FocusReorderGrip(to);
        return true;
    }

    // After a keyboard move + rebuild, put focus back on the grip of the row now at slot `nth`.
    private void FocusReorderGrip(int nth)
    {
        RenderNode? container = null;
        void FindC(RenderNode n) { if (container is null && n.Element?.ClassList.Contains("cupri-reorder") == true) container = n; foreach (var c in n.Children) FindC(c); }
        FindC(_root);
        if (container is null) return;

        var rows = new List<RenderNode>();
        foreach (var c in container.Children) if (c.Element?.ClassList.Contains("cupri-reorder-item") == true) rows.Add(c);
        if (nth < 0 || nth >= rows.Count) return;

        RenderNode? grip = null;
        void FindG(RenderNode n) { if (grip is null && n.Element?.ClassList.Contains("cupri-reorder-handle") == true) grip = n; foreach (var c in n.Children) FindG(c); }
        FindG(rows[nth]);
        if (grip is null) return;

        var idx = Focusables().FindIndex(n => ReferenceEquals(n, grip));
        if (idx >= 0) { _kbIndex = idx; _focusVisible = true; }
    }

    // Tree: expand/collapse the focused item by toggling its twist, but only when it isn't already in the
    // requested state (→ expands a closed item, ← collapses an open one; otherwise the key is consumed).
    private bool TreeExpand(RenderNode twist, bool expand)
    {
        RenderNode? item = twist;
        while (item is not null && item.Element?.HasAttribute("aria-expanded") != true) item = item.Parent;
        if (item?.Element is not { } el) return true;
        var expanded = el.GetAttribute("aria-expanded") == "true";
        return expand == expanded || ActivateFocused(); // already there → consume; else toggle the twist
    }

    // Grab a split divider: find the panels either side of it, and cache their sizes + total grow so the
    // drag can trade size between just those two while the rest of the split stays put.
    private bool StartSplit(RenderNode divider, float x, float y)
    {
        if (divider.Parent is not { } split) return false;
        var kids = split.Children;
        var di = kids.IndexOf(divider);
        RenderNode? a = null, b = null;
        for (var i = di - 1; i >= 0; i--) if (kids[i].Element?.ClassList.Contains("cupri-split-panel") == true) { a = kids[i]; break; }
        for (var i = di + 1; i < kids.Count; i++) if (kids[i].Element?.ClassList.Contains("cupri-split-panel") == true) { b = kids[i]; break; }
        if (a is null || b is null) return false;

        _splitVertical = split.Element?.ClassList.Contains("vertical") == true;
        _splitA = a; _splitB = b;
        _splitStart = _splitVertical ? y : x;
        _splitPA0 = _splitVertical ? a.Height : a.Width;
        _splitPB0 = _splitVertical ? b.Height : b.Width;
        _splitGSum = (a.SplitGrow ?? a.Style.FlexGrow) + (b.SplitGrow ?? b.Style.FlexGrow);
        return true;
    }

    // Trade size between the two panels by the drag delta, clamped so neither collapses; re-express as
    // flex-grow (their sum unchanged) so layout redistributes and everything else holds.
    private bool MoveSplit(float x, float y)
    {
        if (_splitA is not { } a || _splitB is not { } b) return false;
        var total = _splitPA0 + _splitPB0;
        if (total <= 1f || _splitGSum <= 0f) return true;
        var delta = (_splitVertical ? y : x) - _splitStart;
        var newPA = Math.Clamp(_splitPA0 + delta, 40f, total - 40f);
        a.SplitGrow = _splitGSum * newPA / total;
        b.SplitGrow = _splitGSum - a.SplitGrow.Value;
        return true; // re-layout on repaint; no rebuild
    }

    // Grab a resizable table's column boundary: the press must be on a header cell (of a table marked
    // data-cupri-colresize), within ~7px of that cell's right edge, and not on the last column (which is
    // left flexible). Caches the column's content width so the drag is jump-free (flex-basis is content-box).
    private bool StartColumnResize(RenderNode cell, float x)
    {
        if (_model is null || ColumnBoundaryAt(cell, x) is not { } at) return false;
        var (table, col) = at;
        _colPath = table.Element!.GetAttribute("data-cupri-colresize")!;
        _colIndex = col; _colStartX = x;
        _colStartW = cell.ContentBoxWidth;                            // flex-basis we write is content-box
        _colList = (table.Element.GetAttribute("resize") ?? "").Split(',');

        // How wide this column may become: the table's content box, less what the OTHER columns need —
        // the ones to its left keep the width they have, the ones to its right keep at least the floor.
        // Without this a column could be dragged far past the table, collapsing every other column and
        // overflowing the container (dragging one boundary right made a 360px table 828px wide).
        var cells = cell.Parent!.Children.Where(c => c.Element?.LocalName == "cupri-cell").ToList();
        var before = 0f;
        for (var i = 0; i < col && i < cells.Count; i++) before += cells[i].Width;
        var after = Math.Max(0, cells.Count - col - 1);
        // Reserve in BORDER-box terms (what actually occupies the row), then convert the remainder back
        // to the content width we write as flex-basis. Cells share the component's padding, so this
        // cell's insets stand in for the others'.
        var insets = cell.Width - cell.ContentBoxWidth;               // this cell's own padding/border
        var free = table.ContentBoxWidth - before - after * (MinColumnW + insets);
        _colMaxW = Math.Max(MinColumnW, free - insets);
        return true;
    }

    // Is the pointer on a resizable table's column boundary — a non-last header cell's right edge (±7px)?
    // Returns the table node + column index if so. Shared by the grab (StartColumnResize) and CursorAt.
    private static (RenderNode Table, int Col)? ColumnBoundaryAt(RenderNode cell, float x)
    {
        if (cell.Element is not { LocalName: "cupri-cell" } ce) return null;
        if (ce.GetAttribute("data-col") is not { } cs || !int.TryParse(cs, out var col)) return null;
        if (cell.Parent?.Element?.ClassList.Contains("header") != true) return null;

        RenderNode? table = cell.Parent;
        while (table is not null && table.Element?.GetAttribute("data-cupri-colresize") is not { Length: > 0 }) table = table.Parent;
        if (table is null) return null;

        var lastCol = cell.Parent!.Children.Count(c => c.Element?.LocalName == "cupri-cell") - 1;
        if (col >= lastCol) return null;                              // the last column fills remaining width

        var box = HitTesting.AbsoluteBox(cell);
        return x >= box.X + box.W - 7f && x <= box.X + box.W + 7f ? (table, col) : null;
    }

    // Write the dragged column's new content width into the bound list and rebuild; Expand re-applies it to
    // every row's matching cell, so the whole column tracks the pointer in step (like the slider drag).
    private bool MoveColumnResize(float x)
    {
        if (_colPath is null || _model is null) return false;
        var w = Math.Clamp(_colStartW + (x - _colStartX), MinColumnW, _colMaxW);
        var list = _colList.ToList();
        while (list.Count <= _colIndex) list.Add("");
        list[_colIndex] = ((int)MathF.Round(w)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        BindingEngine.TrySet(_model, _colPath, string.Join(',', list));
        Refresh();
        return true;
    }

    public bool DispatchPointerMove(float x, float y) => Bump(DispatchPointerMoveCore(Zc(x), Zc(y)));
    private bool DispatchPointerMoveCore(float x, float y)
    {
        EnsureLaidOut();
        if (_colPath is not null) return MoveColumnResize(x);
        if (_splitA is not null) return MoveSplit(x, y);
        if (_reorderItems is not null) return MoveReorder(x, y);
        if (_resizeDrag is { } rz)
        {
            var mode = rz.Style.Resize;
            if (mode is ResizeMode.Both or ResizeMode.Horizontal)
                rz.ResizeW = ClampResize(rz, _resizeW0 + (x - _resizeX0), horizontal: true);
            if (mode is ResizeMode.Both or ResizeMode.Vertical)
                rz.ResizeH = ClampResize(rz, _resizeH0 + (y - _resizeY0), horizontal: false);
            return true; // re-layout on repaint; no rebuild
        }
        if (_scrollDrag is { } sd)
        {
            var boxH = sd.ContentBoxHeight;
            var thumbH = MathF.Max(28f, boxH * boxH / sd.ScrollContentHeight);
            var travel = boxH - thumbH;
            if (travel > 0.5f)
                sd.ScrollY = Math.Clamp(_scrollDragScroll0 + (y - _scrollDragY0) / travel * sd.MaxScrollY, 0, sd.MaxScrollY);
            // If it's a virtual list, re-windowing rebuilds the tree — which runs CancelOrphanedPointerDrags
            // and drops _scrollDrag, so the drag would die after one frame. Re-link to the rebuilt scroller
            // by its stable data-virtual-key so the thumb keeps tracking the pointer.
            var vkey = sd.Element?.GetAttribute("data-virtual-key");
            if (RewindowVirtual(sd) && vkey is { Length: > 0 })
                _scrollDrag = FindByVirtualKey(_root, vkey);
            return true;          // scroll is paint-time → repaint (RewindowVirtual rebuilds if needed)
        }
        // Dragging the window by a data-window-drag element. The press point is NOT updated as we go:
        // once the host has moved the window, the pointer is back over the point it grabbed, so the
        // next event's distance is measured from the same origin again. Updating it would halve every
        // movement. Rounded to whole pixels because a window position is an integer.
        if (_windowDrag)
        {
            var dx = (int)MathF.Round(x - _windowDragX);
            var dy = (int)MathF.Round(y - _windowDragY);
            if (dx != 0 || dy != 0) WindowMoveRequested?.Invoke(new Interaction.WindowMove(dx, dy));
            return false;                        // the window moved; nothing in the document changed
        }
        if (_dragging)
        {
            if (!ApplySliderValue(x)) return false;
            Refresh();
            return true;
        }
        // Drag-select text: extend the selection caret to the pointer (anchor stays put).
        if (_textDrag && _focusKey is not null && FindFocused(_root) is { } field)
        {
            _caret = CaretFromPoint(field, FindCaretAnchor(field) ?? field, x, y);
            _caretMoved = true; // auto-scroll if the drag runs past the visible edge
            return true; // caret/selection only → repaint, no rebuild
        }
        return UpdateHover(x, y);
    }

    /// <summary>Pointer up: end any slider drag, scrollbar drag, or text drag-select.</summary>
    /// <summary>Pointer released: end any drag and clear the :active press. Returns true if the press
    /// state cleared (→ repaint needed to un-press).</summary>
    public bool DispatchPointerUp(float x, float y) => Bump(DispatchPointerUpCore(Zc(x), Zc(y)));
    private bool DispatchPointerUpCore(float x, float y)
    {
        if (_reorderItems is not null) { EndReorder(); return true; }
        if (_splitA is not null) { _splitA = null; _splitB = null; return true; }
        if (_colPath is not null) { _colPath = null; return true; }
        _dragging = false; _dragSeek = null; _dragUndecided = false; _windowDrag = false; _textDrag = false; _scrollDrag = null; _resizeDrag = null; return ClearActive();
    }

    /// <summary>Scroll wheel: scroll the nearest scrollable element under the pointer by pixels.</summary>
    public bool DispatchWheel(float x, float y, float pixelDelta) => Bump(DispatchWheelCore(Zc(x), Zc(y), pixelDelta, 0f));

    /// <summary>Scroll under the pointer, on either axis. <paramref name="horizontalDelta"/> is
    /// positive-right, matching the vertical convention (positive-down); a touch drag supplies both
    /// so a box that overflows diagonally follows the finger.</summary>
    public bool DispatchWheel(float x, float y, float pixelDelta, float horizontalDelta) =>
        Bump(DispatchWheelCore(Zc(x), Zc(y), pixelDelta, horizontalDelta));

    /// <summary>Scroll the scrollers CAPTURED at gesture start (from <see cref="ScrollTargetAt"/>)
    /// rather than whatever currently lies under the finger.
    ///
    /// A wheel is dispatched at a live pointer, so re-resolving the target per event is right for a
    /// mouse. A touch drag is the opposite: the finger holds one point while the CONTENT moves
    /// beneath it. Re-hit-testing then hands the gesture to whatever happens to slide under that
    /// point — dragging the page from well above a virtualised list would scroll the page until the
    /// list arrived under the finger, at which point the list silently took over the drag.
    ///
    /// The axes are captured separately because the nearest sideways scroller is rarely the nearest
    /// vertical one.</summary>
    public bool ScrollCaptured(string? verticalPath, string? horizontalPath,
                               float pixelDelta, float horizontalDelta)
    {
        EnsureLaidOut();
        DismissContextMenuForScroll();
        return Bump(ScrollCore(horizontalPath is null ? null : NodeAtPath(horizontalPath),
                               verticalPath is null ? null : NodeAtPath(verticalPath),
                               pixelDelta, horizontalDelta));
    }

    private void DismissContextMenuForScroll()
    {
        if (_ctxOpen || _ctxCustomIndex >= 0) { _ctxOpen = false; _ctxCustomIndex = -1; Refresh(); }
    }

    private bool DispatchWheelCore(float x, float y, float pixelDelta, float horizontalDelta)
    {
        EnsureLaidOut();
        DismissContextMenuForScroll();
        var hit = HitTesting.HitTest(_root, x, y);
        return ScrollCore(hit, hit, pixelDelta, horizontalDelta);
    }

    /// <summary>The chaining itself, shared by the pointer-resolved and path-resolved entry points.
    /// Each axis starts from its own node and chains outward through ancestors.</summary>
    private bool ScrollCore(RenderNode? xStart, RenderNode? yStart, float pixelDelta, float horizontalDelta)
    {
        var hit = xStart;
        var moved = false;

        // The axes chain INDEPENDENTLY: the nearest ancestor that can move on each one takes that
        // axis. A row of cards inside a vertically scrolling page is the ordinary case — the row
        // takes the sideways part of a diagonal drag while the page takes the up-down part.
        if (Math.Abs(horizontalDelta) > 0.01f)
            for (var n = hit; n is not null; n = n.Parent)
            {
                if (!n.IsScrollableX) continue;
                var before = n.ScrollX;
                n.ScrollX = Math.Clamp(n.ScrollX + horizontalDelta, 0, n.MaxScrollX);
                if (Math.Abs(n.ScrollX - before) > 0.01f) { moved = true; break; }
                // At its edge: chain outward, as browsers do.
            }

        if (Math.Abs(pixelDelta) > 0.01f)
            for (var n = yStart; n is not null; n = n.Parent)
            {
                if (!n.IsScrollable) continue;
                var before = n.ScrollY;
                n.ScrollY = Math.Clamp(n.ScrollY + pixelDelta, 0, n.MaxScrollY);
                if (RewindowVirtual(n)) return true;                 // a virtual list re-windowed (rebuilt)
                if (Math.Abs(n.ScrollY - before) > 0.01f) { moved = true; break; }
                // This scroller is already at its edge in that direction — chain to the next scrollable
                // ancestor, as browsers do. Without this, a wheel over an inner scroller (a table with
                // overflow:scroll) went dead once it hit bottom instead of scrolling the page.
            }
        return moved;
    }

    // A scrolled <cupri-virtual> list: once it has moved ~2 rows since its last window, record the offset and
    // rebuild so the newly-exposed rows are built (a 4-row buffer covers the rows in between). Returns true if
    // it rebuilt. Scroll offsets survive the rebuild (CaptureScroll), so the list stays put + re-windows.
    // Find the (rebuilt) virtual-list scroller by its stable key — used to re-link an in-flight
    // scrollbar drag across the re-window rebuild.
    private static RenderNode? FindByVirtualKey(RenderNode n, string key)
    {
        if (n.Element?.GetAttribute("data-virtual-key") == key) return n;
        foreach (var c in n.Children) { var f = FindByVirtualKey(c, key); if (f is not null) return f; }
        return null;
    }

    private bool RewindowVirtual(RenderNode n)
    {
        if (n.Element?.GetAttribute("data-virtual-key") is not { Length: > 0 } key) return false;
        // Threshold ONE estimated row (the window carries a six-row buffer each side): with variable
        // pitches drift is pixels, not rows, and rebuilding a windowed list costs ~a millisecond.
        // Clamped — a follow-tail restore parks ScrollY at float.MaxValue until layout clamps it,
        // and storing that raw would poison every later drift comparison.
        var itemH = VirtualItemH(n.Element);
        var scrollY = Math.Clamp(n.ScrollY, 0, n.MaxScrollY);
        if (Math.Abs(scrollY - _virtualScroll.GetValueOrDefault(key)) < itemH) return false;
        _virtualScroll[key] = scrollY;
        // The pin question ("is the user at the bottom?") must be answered from where they ARE:
        // this rebuild IS the scroll that may have just left the bottom, and the per-frame capture
        // has not run since — a stale true would pin the wheel-up straight back down (#67).
        _virtualAtBottom[key] = scrollY >= n.MaxScrollY - 1f;
        Rebuild();
        // The caller is mid-gesture — a fling step, a scrollbar drag — and the very next thing it
        // touches is the FRESH tree's geometry (IsScrollable, MaxScrollY), which an unlaid tree
        // reports as zero: a fling died on the first re-window it crossed because the rebuilt
        // scroller "wasn't scrollable" for one frame. Lay the new tree out before returning.
        EnsureLaidOut();
        return true;
    }

    private static double VirtualItemH(AngleSharp.Dom.IElement el) =>
        double.TryParse(el.GetAttribute("item-height"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 40;

    // ---- variable-height virtualisation (#67) ------------------------------------------------

    /// <summary>After layout: record each materialised virtual row's real pitch (margin-box height)
    /// into the per-list cache, and keep the scroll HONEST while doing it. A row measured for the
    /// first time ABOVE the viewport top changes what the spacer above should have been — everything
    /// on screen would jump by the difference — so the offset shifts by the same amount in the same
    /// frame (scroll anchoring). Runs every frame; a fixed-height list measures pitch == estimate
    /// everywhere and every correction is zero.</summary>
    private void CaptureVirtualHeights(RenderNode n)
    {
        if (n.Element?.GetAttribute("data-virtual-key") is { Length: > 0 } key && n.Style.Overflow == OverflowMode.Scroll)
            CaptureVirtualList(n, key);
        foreach (var c in n.Children) CaptureVirtualHeights(c);
    }

    private void CaptureVirtualList(RenderNode n, string key)
    {
        if (!_virtualHeights.TryGetValue(key, out var pitches)) _virtualHeights[key] = pitches = new List<float>();

        // Pitches are wrap-determined: a different content width re-wraps every bubble, so the whole
        // cache is stale, not just the visible rows.
        var width = n.ContentBoxWidth;
        if (_virtualWidth.TryGetValue(key, out var w0) && MathF.Abs(w0 - width) > 0.5f) pitches.Clear();
        _virtualWidth[key] = width;

        var est = (float)VirtualItemH(n.Element!);
        var scrollY = Math.Clamp(n.ScrollY, 0, n.MaxScrollY);
        var idx = int.TryParse(n.Element!.GetAttribute("data-virtual-first"), out var f) ? f : 0;
        var delta = 0f;
        foreach (var row in n.Children)
        {
            if (row.Element is null || row.Style.Display == DisplayType.None
                || row.Style.Position is PositionType.Absolute or PositionType.Fixed
                || row.Element.HasAttribute("data-virtual-spacer")) continue;
            var pitch = row.MarginTop + row.Height + row.MarginBottom;
            while (pitches.Count <= idx) pitches.Add(0f);
            if (MathF.Abs(pitches[idx] - pitch) > 0.01f)
            {
                // What the current spacer assumed for this row: its previous measurement, else the
                // estimate. A row wholly above the viewport top has its size error baked into what
                // the user is looking at — compensate so the visible content stays put.
                var assumed = pitches[idx] > 0 ? pitches[idx] : est;
                if (row.Y - n.ContentTopInset + pitch <= scrollY + 0.5f) delta += pitch - assumed;
                pitches[idx] = pitch;
            }
            idx++;
        }

        if (_virtualPinned.Remove(key))
        {
            // The binder pinned this list to its (estimate-based) bottom; the layout knows the real
            // one — snap exactly, so the last row sits flush and AtBottom below records true.
            n.ScrollY = n.MaxScrollY;
            _virtualScroll[key] = n.ScrollY;
        }
        else if (MathF.Abs(delta) > 0.01f)
        {
            n.ScrollY = Math.Clamp(scrollY + delta, 0, n.MaxScrollY);
            if (_virtualScroll.TryGetValue(key, out var stored)) _virtualScroll[key] = stored + delta;
        }

        // Recorded, not enforced: the pin itself belongs to the binder (anchor="bottom") and the
        // restore path (data-follow-tail) — this is only the "was the user at the bottom" fact.
        _virtualAtBottom[key] = Math.Clamp(n.ScrollY, 0, n.MaxScrollY) >= n.MaxScrollY - 1f;
    }

    /// <summary>Tell the engine that <paramref name="count"/> rows were inserted into a virtualised
    /// collection at <paramref name="index"/> — the "load older history" prepend in a chat log —
    /// BEFORE calling <see cref="Refresh"/>. The measured-pitch cache shifts to stay index-aligned,
    /// and when the insertion lands above the viewport the scroll offset advances by the inserted
    /// rows' estimated extent, so the content on screen stays put instead of leaping down by the new
    /// rows. Appends need no call: existing indices do not move.</summary>
    public void VirtualListInserted(string repeatPath, int index, int count)
    {
        if (count <= 0 || index < 0) return;
        var est = 40.0;
        if (_root is not null && FindByVirtualKey(_root, repeatPath) is { Element: { } el }) est = VirtualItemH(el);

        var pitches = _virtualHeights.GetValueOrDefault(repeatPath);
        if (pitches is not null && index <= pitches.Count)
            for (var k = 0; k < count; k++) pitches.Insert(index, 0f);

        // Above the viewport ⇔ the content prefix up to the insertion point fits inside the current
        // offset (at the very top — the usual prepend — index 0 always qualifies).
        var scrollY = _virtualScroll.GetValueOrDefault(repeatPath);
        double prefix = 0;
        for (var i = 0; i < index; i++)
            prefix += pitches is not null && i < pitches.Count && pitches[i] > 0 ? pitches[i] : est;
        if (prefix <= scrollY + 0.5)
        {
            var compensated = scrollY + count * est;
            _virtualScroll[repeatPath] = compensated;         // the next bind windows against this
            _pendingVirtualScroll[repeatPath] = compensated;  // …and the rebuilt node starts here
        }
    }

    // Toggle data-hover on the hovered element + ancestors, then re-resolve styles (no full rebuild).
    private bool UpdateHover(float x, float y)
    {
        var hit = HitTesting.HitTest(_root, x, y);
        _lastHit = hit; _lastHitRoot = _root; _lastHitX = x; _lastHitY = y; // reused by CursorAt
        var target = hit?.Element;
        if (_hoverChain.Count > 0 && ReferenceEquals(_hoverChain[0], target)) return false;

        foreach (var e in _hoverChain) e.RemoveAttribute("data-hover");
        _hoverChain.Clear();
        for (var e = target; e is not null; e = e.ParentElement)
        {
            e.SetAttribute("data-hover", "");
            _hoverChain.Add(e);
        }
        ReStyle();
        return true;
    }

    // Mark data-active (CSS :active) on the pressed element + ancestors — set on pointer-down (the
    // caller restyles), cleared on pointer-up. Holds while the button is down, unlike hover. Just
    // mutates the DOM (no restyle here) so it doesn't disturb the click's in-progress hit logic.
    private void SetActive(IElement? target)
    {
        foreach (var e in _activeChain) e.RemoveAttribute("data-active");
        _activeChain.Clear();
        for (var e = target; e is not null; e = e.ParentElement)
        {
            e.SetAttribute("data-active", "");
            _activeChain.Add(e);
        }
    }

    private bool ClearActive()
    {
        if (_activeChain.Count == 0) return false;
        foreach (var e in _activeChain) e.RemoveAttribute("data-active");
        _activeChain.Clear();
        ReStyle();
        return true;
    }

    /// <summary>Re-resolve styles on the existing DOM (for :hover/:active) without re-parsing/binding.</summary>
    private void ReStyle()
    {
        if (_dom is null) return;
        // Restyle (hover/active) rebuilds the tree too, so preserve scroll offsets — otherwise any
        // mouse move over the page snaps a scrolled field back to the top.
        var scroll = CaptureScroll();
        var resolver = new StyleResolver(_rules, _viewportWidth, _viewportHeight);
        _root = resolver.BuildTree(_dom);
        _hasViewportUnits |= resolver.SawViewportUnit;
        _layoutDirty = true; // fresh tree: no geometry until the next layout
        RestoreScroll(scroll);
        _transitions.Detect(_root, _laidOutWidth, _laidOutHeight); // hover/focus/class change → (re)start any transitions that flipped
    }

    private static double ParseAttr(IElement el, string name, double fallback) =>
        double.TryParse(el.GetAttribute(name), CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // Selector matching for OnClick handlers.
    //
    // NOT `el.Matches(selector)`: that resolves a CSS selector parser off the element's browsing
    // context at call time, and in a TRIMMED WebAssembly publish that resolution fails — it threw, the
    // old catch swallowed it, and every selector-registered OnClick silently became a no-op on the web
    // host (dead sidebar nav, toast buttons, swatches…) while behaving perfectly on desktop. Compiling
    // the selector up front with CssSelectorParser is the same path the style resolver uses — which
    // demonstrably survives trimming, since the published build styles the page correctly — and it also
    // removes a per-click/per-mouse-move parse.
    private static readonly AngleSharp.Css.Parser.CssSelectorParser SelectorParser = new();

    private static AngleSharp.Css.Dom.ISelector? CompileSelector(string selector)
    {
        try { return SelectorParser.ParseSelector(selector); }
        catch { return null; } // unparseable selector: the handler simply never matches
    }

    private static bool Matches(IElement el, AngleSharp.Css.Dom.ISelector? compiled) =>
        compiled is not null && compiled.Match(el, null);

    /// <summary>Convenience CPU-raster render to an image (headless/tests).</summary>
    public SKImage RenderToImage(int width, int height, SKColor? clear = null)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(clear ?? SKColors.White);
        Render(surface.Canvas, width, height);
        surface.Canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>Render a frame to an RGBA8888 byte buffer (<c>width*height*4</c>) — the canonical
    /// "embed me in another surface" entry point (HTML canvas, a game texture, …). Clears to
    /// <paramref name="clear"/> (default transparent, for overlays). Set <paramref name="straightAlpha"/>
    /// for consumers that want NON-premultiplied alpha (HTML <c>ImageData</c>, Unity <c>RGBA32</c>);
    /// leave it false for desktop compositors, which want premultiplied — Skia's native output.
    /// (A host needing zero per-frame allocation can instead call <see cref="Render"/> into its own
    /// surface and read its pixels directly.)</summary>
    public byte[] RenderToPixels(int width, int height, SKColor? clear = null, bool straightAlpha = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(clear ?? SKColors.Transparent);
        Render(surface.Canvas, width, height);
        surface.Canvas.Flush();
        using var img = surface.Snapshot();
        // Read back in the requested alpha type — Skia converts premultiplied → straight for us.
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888,
            straightAlpha ? SKAlphaType.Unpremul : SKAlphaType.Premul));
        img.ReadPixels(bmp.PeekPixels(), 0, 0);
        return bmp.Bytes;
    }

    public void Dispose()
    {
        foreach (var player in _videoPlayers.Values)
            try { player.Dispose(); } catch { /* a dying decoder must not block disposal */ }
        _videoPlayers.Clear();
        _fonts.Dispose();
        _images.Dispose();
        _dom?.Dispose();
        _templateDom?.Dispose();
    }
}
