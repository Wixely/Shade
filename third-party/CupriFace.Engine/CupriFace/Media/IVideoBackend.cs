namespace CupriFace.Media;

/// <summary>
/// A <c>&lt;cupri-video&gt;</c> source, resolvable exactly like an image: an <b>embedded</b>
/// asset (bare name, the registered app assembly), a <b>disk</b> file (<c>file://</c> or a
/// path), an inline <c>data:</c> URI, or a <b>web URL</b> fetched under the document's
/// <c>UseImageUrlOptions</c> policy (https-only, size cap, timeout by default). The document
/// builds these — one trust model for all media, chosen per element by the developer.
/// </summary>
public readonly struct VideoSource
{
    public string Src { get; }
    private readonly System.Reflection.Assembly? _assembly;
    private readonly Resources.CupriSourceOptions? _urlOptions;

    internal VideoSource(string src, System.Reflection.Assembly? assembly, Resources.CupriSourceOptions? urlOptions)
    {
        Src = src;
        _assembly = assembly;
        _urlOptions = urlOptions;
    }

    /// <summary>For tests/tools opening a source outside a document (no embedded assembly,
    /// default URL policy).</summary>
    public VideoSource(string src) : this(src, null, null) { }

    /// <summary>Remote sources should not be fetched on the UI thread — backends open them
    /// deferred (poster stays up, playback starts when the bytes land), mirroring the image
    /// store's async remote loads.</summary>
    public bool IsRemote => Resources.SourceResolver.IsRemote(Src);

    /// <summary>Resolve to bytes through the shared pipeline; null when unresolvable.</summary>
    public byte[]? LoadBytes() => Resources.SourceResolver.Load(Src, _assembly, _urlOptions);
}

/// <summary>
/// Opens video sources for <c>&lt;cupri-video&gt;</c>. The engine defines only this seam — it has
/// no codecs. Implementations attach at the HOST composition root (never inside a portable app
/// class, which is shared across hosts): the desktop Viewer registers <c>CupriFace.Media</c>'s
/// WebM backend via <c>DesktopHost.Run(app, d =&gt; d.UseVideo(...))</c>; the web host registers a
/// browser-decoded backend. No backend registered → the element shows its poster and the
/// controls do nothing.
/// </summary>
public interface IVideoBackend
{
    /// <summary>Open a source. Throwing is allowed for an unusable one — the document catches it
    /// and leaves the poster up.</summary>
    IVideoPlayer Open(VideoSource source);
}

/// <summary>
/// One playing (or paused) video. The frames flow through <see cref="Surface"/> into the
/// element's <c>data-cupri-surface</c>; transport state is read by the document each rebuild to
/// label the controls.
///
/// Threading: transport calls (<see cref="Play"/>/<see cref="Pause"/>/setters) arrive on the UI
/// thread, synchronously inside input dispatch — the web backend RELIES on that (the browser
/// only allows unmuted playback while a user gesture is on the stack, so the call chain
/// click → engine → <c>video.play()</c> must never defer). <see cref="Ended"/> MAY be raised
/// from any thread; the document coalesces it and reacts on its own thread.
/// </summary>
public interface IVideoPlayer : IDisposable
{
    /// <summary>The live pixel producer the element paints (see <see cref="Paint.ISurfaceSource"/>).</summary>
    Paint.ISurfaceSource Surface { get; }

    void Play();
    void Pause();
    bool Playing { get; }

    bool Muted { get; set; }
    /// <summary>0..1.</summary>
    double Volume { get; set; }
    bool Loop { get; set; }

    /// <summary>Seconds; 0 until known.</summary>
    double Duration { get; }
    /// <summary>Seconds; setting seeks.</summary>
    double Position { get; set; }

    /// <summary>Playback reached the end (not raised when looping). Any thread.</summary>
    event Action? Ended;

    /// <summary>One human-readable line of live playback internals (decode time, dropped frames,
    /// queue depth — whatever the backend can honestly measure), for diagnostics surfaces. Default
    /// null: a backend with nothing to report costs nothing.</summary>
    string? DiagnosticsSummary => null;
}
