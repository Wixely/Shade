using CupriFace.Components;
using CupriFace.Resources;
using CupriFace.Text;
using SkiaSharp;

namespace CupriFace;

/// <summary>How the document is presented into the window (see <see cref="CupriApp.Present"/>).</summary>
/// <summary>
/// What <see cref="CupriApp.Present"/> hands the host: the logical viewport the document is laid
/// out at, and a scale factor the host applies to the canvas (and divides pointer coordinates by).
///
/// <para>Those three numbers can express every scaling strategy, which is why the engine takes them
/// rather than a mode enum — but "can express" is not "explains", and an app author staring at three
/// floats has to derive the arithmetic themselves. The named constructors below are that arithmetic,
/// written once: <see cref="Responsive"/>, <see cref="Fixed"/>, <see cref="Zoom"/> and
/// <see cref="Hybrid"/>. They used to live in a sample, where nobody looking at the engine could
/// find them.</para>
/// </summary>
/// <param name="LogicalWidth">Viewport width the document lays out at, in CSS pixels.</param>
/// <param name="LogicalHeight">Viewport height the document lays out at, in CSS pixels.</param>
/// <param name="Scale">Factor the host applies when painting; 1 means logical == physical.</param>
public readonly record struct PresentInfo(float LogicalWidth, float LogicalHeight, float Scale)
{
    /// <summary>Scale factors outside this range stop being a design choice and start being a bug —
    /// a zero or a NaN silently lays the document out at an absurd size rather than throwing.</summary>
    public const float MinScale = 0.25f, MaxScale = 4f;

    /// <summary>
    /// <b>Responsive</b> (the default): lay out at the window, scale 1. The page reflows as it
    /// resizes, exactly like a web page — more window means more content visible, not bigger content.
    /// </summary>
    public static PresentInfo Responsive(float windowWidth, float windowHeight) =>
        new(windowWidth, windowHeight, 1f);

    /// <summary>
    /// <b>Fixed</b>: always lay out at the design size, scale 1. The window revealing more space
    /// reveals background rather than reflowing. Right for a layout that is only correct at one size.
    /// </summary>
    public static PresentInfo Fixed(float designWidth, float designHeight) =>
        new(designWidth, designHeight, 1f);

    /// <summary>
    /// <b>Zoom</b>: keep the physical window, shrink the logical viewport, and let the host scale up
    /// — the effect of changing display DPI. Text and vectors are re-rasterised at the new size, so
    /// they stay crisp rather than being resampled.
    /// </summary>
    /// <param name="factor">Clamped to [<see cref="MinScale"/>, <see cref="MaxScale"/>].</param>
    public static PresentInfo Zoom(float windowWidth, float windowHeight, float factor)
    {
        var z = float.IsFinite(factor) ? Math.Clamp(factor, MinScale, MaxScale) : 1f;
        return new(windowWidth / z, windowHeight / z, z);
    }

    /// <summary>
    /// <b>Hybrid zoom</b>: zoom to whichever axis is tighter, and let the other one reflow.
    ///
    /// <para><c>z = min(windowW / designW, windowH / designH)</c>. The tighter axis ends up at design
    /// scale — so a layout tuned for <paramref name="designWidth"/> is never squeezed below it — while
    /// the roomier axis gets extra logical space and reflows into it. On a phone that usually means
    /// "fill the width, scroll the length"; on a wide monitor, "fill the height, spread out".</para>
    ///
    /// <para>The usual choice for mixed content, because pure <see cref="Zoom"/> letterboxes and pure
    /// <see cref="Responsive"/> lets a narrow window crush a layout that assumed room.</para>
    /// </summary>
    /// <example>
    /// <code>
    /// public override int Width =&gt; 940;      // the size the layout was designed at
    /// public override int Height =&gt; 720;
    /// public override PresentInfo Present(float w, float h) =&gt; PresentInfo.Hybrid(w, h, Width, Height);
    /// </code>
    /// </example>
    public static PresentInfo Hybrid(float windowWidth, float windowHeight,
                                     float designWidth, float designHeight)
    {
        // A zero or negative design size would divide to infinity and lay out at nothing; treat it as
        // "no design size given" and reflow, which is the harmless answer rather than a blank window.
        if (designWidth <= 0 || designHeight <= 0) return Responsive(windowWidth, windowHeight);
        return Zoom(windowWidth, windowHeight,
                    MathF.Min(windowWidth / designWidth, windowHeight / designHeight));
    }
}

/// <summary>
/// A portable application definition: markup, styles, components, model, and behaviour —
/// with **no** windowing or platform dependency. The same subclass runs on a desktop host
/// (GL/SDL window) or a web host (WASM → &lt;canvas&gt;); "exporting to a website" is just
/// recompiling this class against the web host. See <c>DesktopHost</c> and <c>CupriView</c>.
/// </summary>
public abstract class CupriApp
{
    /// <summary>Raised when application UI wants to place text on the host clipboard. Desktop and
    /// web hosts can service the request without leaking platform clipboard APIs into the app.</summary>
    public event Action<string>? ClipboardWriteRequested;

    /// <summary>Ask the current host to place text on its clipboard. Returns false when the host
    /// has not supplied clipboard support.</summary>
    protected bool TryWriteClipboard(string text)
    {
        var handler = ClipboardWriteRequested;
        if (handler is null)
        {
            return false;
        }

        handler(text);
        return true;
    }

    /// <summary>Where the markup is loaded from (embedded resource, file, or URL). Preferred over
    /// overriding <see cref="Html"/> directly — see <see cref="EmbeddedAsset"/>.</summary>
    protected virtual CupriSource? MarkupSource => null;

    /// <summary>Where the stylesheet is loaded from. Optional (an app may inline all CSS in its markup).</summary>
    protected virtual CupriSource? StyleSource => null;

    /// <summary>The document markup. Defaults to reading <see cref="MarkupSource"/>; override either.</summary>
    public virtual string Html => MarkupSource?.ReadText()
        ?? throw new InvalidOperationException($"{GetType().Name} must override Html or MarkupSource.");

    /// <summary>The stylesheet. Defaults to reading <see cref="StyleSource"/> (empty if none).</summary>
    public virtual string Css => StyleSource?.ReadText() ?? "";

    /// <summary>Convenience: an embedded resource in this app's own assembly, e.g.
    /// <c>EmbeddedAsset("Assets/App.html")</c>. The generated <c>Assets</c> class is the typed way.</summary>
    protected CupriSource EmbeddedAsset(string logicalName) => CupriSource.Embedded(GetType().Assembly, logicalName);

    /// <summary>Optional application icon as raw image-file bytes (PNG/JPEG). Hosts adapt it to their
    /// platform: the desktop windows decode it and set the OS window/taskbar icon, the web hosts point
    /// the page's favicon at it, and the Android host badges the recents card with it. Typically an
    /// embedded resource: <c>EmbeddedAsset("Assets/logo.png").ReadBytes()</c>.
    ///
    /// This is the icon of a RUNNING app. The icon of an installed one — a Windows <c>.exe</c>, an
    /// Android launcher entry — is read out of the built file before any of this code exists, so it
    /// is set by the build (<c>ApplicationIcon</c>, an <c>ic_launcher</c> resource), never here.</summary>
    public virtual byte[]? Icon => null;

    /// <summary>The <see cref="Icon"/> encoded as a <c>data:</c> URI, or null when there is no icon.
    /// The media type is sniffed from the bytes rather than assumed, so a JPEG is not announced as a
    /// PNG. Web hosts hand this straight to <c>&lt;link rel="icon"&gt;</c>.</summary>
    public string? IconDataUri
    {
        get
        {
            if (Icon is not { Length: > 3 } bytes) return null;   // virtual: read once, it may hit a resource stream
            var png = bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            return $"data:{(png ? "image/png" : "image/jpeg")};base64,{Convert.ToBase64String(bytes)}";
        }
    }

    public virtual string Title => "CupriFace App";
    public virtual int Width => 800;
    public virtual int Height => 600;
    public virtual SKColor Background => SKColors.White;

    /// <summary>Render with a transparent background so the UI composites over what's behind it — the
    /// desktop (a transparent GL window) or an HTML page (a transparent canvas overlay). The host
    /// clears to transparent instead of <see cref="Background"/>.</summary>
    public virtual bool Transparent => false;

    /// <summary>Borderless window — no title bar or chrome (for HUDs / overlays).</summary>
    public virtual bool Frameless => false;

    /// <summary>Keep the window above other windows (always-on-top).</summary>
    public virtual bool TopMost => false;

    /// <summary>Hide the native window instead of exiting when its close button is pressed, and
    /// provide a notification-area icon for restoring or explicitly closing the application.
    /// Currently implemented by the Windows desktop host.</summary>
    public virtual bool CloseToTray => false;

    /// <summary>Label for the explicit exit command in the notification-area menu.</summary>
    public virtual string TrayCloseLabel => $"Close {Title}";

    /// <summary>Request dark native title-bar and border chrome from desktop hosts that support it.
    /// Other hosts and older operating-system versions safely ignore the preference.</summary>
    public virtual bool DarkWindowChrome => false;

    /// <summary>Opt in to the commit-snapshot render-thread split (DESIGN §7.2): the UI thread builds
    /// the display list and a background thread rasterises it, so rasterisation never blocks input.
    /// Wired for the CPU/SDL software path; the GL path renders inline. Default off.</summary>
    public virtual bool ThreadedRender => false;

    /// <summary>
    /// Run the desktop window DPI-aware, so the OS reports the monitor's scale instead of
    /// bitmap-stretching the process. <b>Default on</b> (#137).
    ///
    /// <para>On Windows this asks for Per-Monitor-V2 before the window is created. It is a REQUEST:
    /// an executable whose manifest already declares an awareness keeps that choice, and so does an
    /// app that set one itself before calling <c>DesktopHost.Run</c> — a library must not overrule
    /// an application that has decided this for itself.</para>
    ///
    /// <para>Turning it off restores the pre-#137 behaviour: the document is laid out in physical
    /// pixels and Windows scales the result, which is softer above 100% but is what apps written
    /// against the old host assumed. The <c>CUPRIFACE_DPI=0</c> environment variable does the same
    /// thing without a rebuild.</para>
    ///
    /// <para>Other hosts ignore this: the browser and Android are told their scale by the platform
    /// and have no awareness mode to declare.</para>
    /// </summary>
    public virtual bool DpiAware => true;

    /// <summary>
    /// Keep following the monitor's scale after startup, so dragging the window between displays of
    /// different DPI re-lays-out at the new one. <b>Default on</b>, checked once per frame tick.
    ///
    /// <para>Only meaningful when <see cref="DpiAware"/> is on — with awareness off the scale is
    /// pinned at 1 and there is nothing to follow. Leaving awareness ON and this OFF is the one
    /// combination worth avoiding: the framebuffer would resize on a monitor change while the scale
    /// used to lay out stayed stale, so the UI would be wrong until the next resize.</para>
    ///
    /// <para>Off is for an app that is content to be correct for the monitor it started on and would
    /// rather not pay a per-frame DPI query — a single-monitor kiosk, say.</para>
    /// </summary>
    public virtual bool TrackMonitorDpi => true;

    /// <summary>Component library available to the markup (defaults to the built-ins).</summary>
    public virtual ComponentRegistry Components => ComponentRegistry.Default();

    /// <summary>Optional bound model for <c>{{...}}</c> interpolation and two-way controls.</summary>
    public virtual object? Model => null;

    /// <summary>Hook to register click handlers etc. after the document is built.</summary>
    public virtual void Configure(CupriDocument document) { }

    /// <summary>
    /// If &gt; 0, the host re-binds the model (<c>doc.Refresh()</c>) at this cadence in seconds,
    /// so computed values that drift over time — e.g. live diagnostics like RAM usage — update
    /// without user interaction. Default 0 = never (re-binds only on interaction).
    /// </summary>
    public virtual double RefreshIntervalSeconds => 0;

    /// <summary>
    /// Given the window size, return the logical viewport the document is laid out at and a scale
    /// factor the host applies. Default = <see cref="PresentInfo.Responsive"/>: lay out at the
    /// window, scale 1, reflowing like a web page.
    ///
    /// <para>Override with one of the named strategies rather than deriving the arithmetic:
    /// <see cref="PresentInfo.Hybrid"/> (zoom the tighter axis, reflow the other — the usual choice
    /// for mixed content), <see cref="PresentInfo.Zoom"/> (DPI-like), or
    /// <see cref="PresentInfo.Fixed"/> (one size, no reflow).</para>
    ///
    /// <para>Called every frame, so it must be cheap and must not allocate.</para>
    /// </summary>
    /// <example>
    /// <code>
    /// public override PresentInfo Present(float w, float h) =&gt; PresentInfo.Hybrid(w, h, Width, Height);
    /// </code>
    /// </example>
    public virtual PresentInfo Present(float windowWidth, float windowHeight) =>
        PresentInfo.Responsive(windowWidth, windowHeight);

    /// <summary>Dev aid: outline every element box in the window (see <see cref="CupriDocument.DebugOverlay"/>).</summary>
    public virtual bool DebugOverlay => false;

    /// <summary>Fonts registered on every document this app creates, before the model binds —
    /// embedded resources, files or URLs, each read once. The files' own family/weight/style are
    /// used; for declared metadata use an <c>@font-face</c> rule in <see cref="Css"/> instead.</summary>
    public virtual IEnumerable<CupriSource> Fonts => [];

    /// <summary>See <see cref="Text.FontPolicy"/>. <see cref="Text.FontPolicy.RegisteredOnly"/> makes
    /// a family with no registered face an error and keeps platform fonts out of glyph fallback —
    /// the setting for output that must not depend on the machine.</summary>
    public virtual FontPolicy FontPolicy => FontPolicy.Platform;

    /// <summary>Build a ready-to-render document — identical on every host.</summary>
    public CupriDocument CreateDocument()
    {
        var doc = CupriDocument.Load(Html, Css).UseComponents(Components).UseImages(GetType().Assembly);
        doc.FontPolicy = FontPolicy;
        foreach (var font in Fonts) doc.LoadFont(font);
        if (Model is { } model) doc.Bind(model);
        doc.DebugOverlay = DebugOverlay;
        Configure(doc);
        return doc;
    }
}
