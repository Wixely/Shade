using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-video src="clip.webm" fit="contain|cover|fill|none" poster="p.png" label="…"
/// autoplay muted loop controls&gt;</c> — a video element. The frames arrive through the live
/// surface lane (<c>data-cupri-surface</c>) from whichever <see cref="Media.IVideoBackend"/> the
/// HOST registered (<c>doc.UseVideo</c>): the desktop WebM decoder or the browser's own decoder
/// on the web host. Until the first frame (or with no backend at all) the <c>poster</c> image
/// shows. The `autoplay` attribute is honored only together with `muted` — the web's own rule,
/// applied on every host so one app behaves identically everywhere.
///
/// Size like an image: CSS width/height, aspect preserved when only one is given, intrinsic
/// video size otherwise. Full-window video is just <c>width:100%; height:100%</c> +
/// <c>fit="cover"</c>. The ⛶ control fullscreens the VIDEO, the way the web does it: the
/// element expands to cover the whole viewport in the top layer (letterboxed on black, controls
/// still overlaid) AND the window goes OS-fullscreen via <c>WindowCommandRequested</c> — so the
/// video fills the screen, not just the window. Escape (or ⛶ again) undoes both.
/// </summary>
public sealed class VideoComponent : ComponentBase
{
    public override string Tag => "cupri-video";
    public override string DefaultCss => """
        .cupri-video { display:block; position:relative; overflow:hidden; background:#0b0d10; }
        .cupri-video-bar { position:absolute; left:0; right:0; bottom:0; display:flex; gap:6px; align-items:center;
                           padding:6px 10px; background:rgba(10,12,16,0.55); }
        .cupri-video-btn { display:inline-flex; align-items:center; justify-content:center;
                           width:32px; height:32px; border-radius:6px; color:#ffffff; }
        .cupri-video-btn:hover { background:rgba(255,255,255,0.18); }
        .cupri-video-btn.disabled { opacity:0.35; }
        .cupri-video-btn.disabled:hover { background:transparent; }
        .cupri-video-time { color:#e6e9ef; font-size:12px; flex:none; }
        .cupri-video-seek { flex:1; padding:8px 2px; cursor:pointer; }
        .cupri-video-seek.disabled { opacity:0.35; cursor:not-allowed; }
        .cupri-video-seek-track { position:relative; height:4px; background:rgba(255,255,255,0.28); border-radius:2px; }
        .cupri-video-seek-fill { position:absolute; top:0; left:0; height:4px; background:var(--cupri-accent,#B87333); border-radius:2px; }
        .cupri-video-seek-thumb { position:absolute; top:-4px; width:12px; height:12px; background:white; border-radius:6px;
                                  box-shadow:0 1px 4px #00000059; }
        .cupri-video-fs { z-index:90; background:#000; }
        """;

    public override void Expand(IElement el)
    {
        var src = Str(el, "src");
        var poster = Str(el, "poster");
        var label = Str(el, "label", src);

        // The element ITSELF is the picture — surface + poster + object-fit live on it, so it
        // sizes exactly like <cupri-image> (CSS width/height, aspect kept, else intrinsic video
        // size). The controls bar overlays its bottom edge; a click anywhere else toggles.
        el.ClassList.Add("cupri-video");
        el.SetAttribute("data-cupri-video", src);
        el.SetAttribute("data-cupri-surface", "video:" + src);
        if (poster.Length > 0) el.SetAttribute("data-cupri-image", poster);
        el.SetAttribute("data-object-fit", Str(el, "fit", "contain"));
        el.SetAttribute("data-video-cmd", "toggle");
        el.SetAttribute("role", "img");
        el.SetAttribute("aria-label", label);
        // Policy flags for the document's video wiring (SyncVideos) — attribute presence only.
        if (Flag(el, "autoplay")) el.SetAttribute("data-video-autoplay", "");
        if (Flag(el, "muted")) el.SetAttribute("data-video-muted", "");
        if (Flag(el, "loop")) el.SetAttribute("data-video-loop", "");

        // The bar: transport, current time, the seek slider (a real role=slider — pointer scrub,
        // arrow keys, AT SetValue all route to the player), duration, fullscreen. The clip's label
        // lives on the ELEMENT's aria-label — the bar has no room for a title once a seek bar
        // exists, which is also every real player's layout.
        el.InnerHtml = !Flag(el, "controls") ? "" : $"""
            <div class='cupri-video-bar'>
              <div class='cupri-video-btn' role='button' aria-label='Play' data-video-role='toggle' data-video-cmd='toggle'>{IconMarkup("play", 18)}</div>
              <div class='cupri-video-btn' role='button' aria-label='Mute' data-video-role='mute' data-video-cmd='mute'>{IconMarkup("volume", 18)}</div>
              <span class='cupri-video-time' data-video-role='time'>0:00</span>
              <div class='cupri-video-seek' role='slider' tabindex='0' aria-label='Seek' data-video-role='seek'
                   aria-valuemin='0' aria-valuemax='0' aria-valuenow='0'>
                <div class='cupri-video-seek-track'>
                  <div class='cupri-video-seek-fill' style='width:0%'></div>
                  <div class='cupri-video-seek-thumb' style='left:0%'></div>
                </div>
              </div>
              <span class='cupri-video-time' data-video-role='duration'>0:00</span>
              <div class='cupri-video-btn' role='button' aria-label='Fullscreen' data-video-role='fullscreen' data-video-cmd='fullscreen'>{IconMarkup("fullscreen", 18)}</div>
            </div>
            """;
    }

    internal static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>No backend on this host (or the source failed to open): the transport controls
    /// get the standard disabled treatment — dimmed, not-allowed cursor, announced by AT —
    /// instead of looking clickable and doing nothing. Fullscreen stays live: it needs no
    /// decoder, only the window.</summary>
    internal static void MarkInert(IElement el)
    {
        foreach (var role in new[] { "toggle", "mute", "seek" })
            if (el.QuerySelector($"[data-video-role='{role}']") is { } control)
            {
                control.ClassList.Add("disabled");
                control.SetAttribute("aria-disabled", "true");
            }
    }

    /// <summary>Make this element THE fullscreen video (the document calls it during each rebuild
    /// while its src is the fullscreen one — the fresh DOM starts in the normal state). The
    /// geometry goes on the INLINE style so it beats the author's own inline width/height/radius
    /// (`style="width:320px"` would defeat a class); appended last, so it wins within the
    /// attribute too. position:fixed puts it in the top layer — painted above everything,
    /// hit-tested first — and the class brings z-index + the black letterbox background.</summary>
    internal static void ApplyFullscreenState(IElement el)
    {
        el.ClassList.Add("cupri-video-fs");
        // transition:none — entering fullscreen SNAPS, like the browser. An author's own
        // width/height transition (the resize demo has one) must not tween into fullscreen.
        el.SetAttribute("style", (el.GetAttribute("style") ?? "")
            + ";position:fixed;left:0;top:0;width:100%;height:100%;border-radius:0;transition:none");
        if (el.QuerySelector("[data-video-role='fullscreen']") is { } button)
        {
            button.InnerHtml = IconMarkup("fullscreen-exit", 18);
            button.SetAttribute("aria-label", "Exit fullscreen");
        }
    }

    /// <summary>Reflect live player state into a freshly rebuilt element's controls (the DOM is
    /// re-parsed every rebuild, so the document calls this for each <c>&lt;cupri-video&gt;</c> it
    /// wired). Glyphs + accessible labels flip together, so Narrator and the pixels agree.</summary>
    internal static void SyncControls(IElement el, Media.IVideoPlayer player)
    {
        if (el.QuerySelector("[data-video-role='toggle']") is { } toggle)
        {
            toggle.InnerHtml = IconMarkup(player.Playing ? "pause" : "play", 18);
            toggle.SetAttribute("aria-label", player.Playing ? "Pause" : "Play");
        }
        if (el.QuerySelector("[data-video-role='mute']") is { } mute)
        {
            mute.InnerHtml = IconMarkup(player.Muted ? "volume-off" : "volume", 18);
            mute.SetAttribute("aria-label", player.Muted ? "Unmute" : "Mute");
        }

        // Seek bar + clocks: position/duration as text, fill/thumb as inline percentages, and the
        // slider's ARIA range so AT reads and SETS the same values the pixels show.
        var duration = player.Duration;
        var position = Math.Clamp(player.Position, 0, duration > 0 ? duration : double.MaxValue);
        if (el.QuerySelector("[data-video-role='time']") is { } time) time.TextContent = FormatTime(position);
        if (el.QuerySelector("[data-video-role='duration']") is { } total) total.TextContent = FormatTime(duration);
        if (el.QuerySelector("[data-video-role='seek']") is { } seek)
        {
            seek.SetAttribute("aria-valuemin", "0");
            seek.SetAttribute("aria-valuemax", Math.Round(duration, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            seek.SetAttribute("aria-valuenow", Math.Round(position, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            seek.SetAttribute("aria-valuetext", $"{FormatTime(position)} of {FormatTime(duration)}");
            var pct = duration > 0 ? Math.Clamp(position / duration * 100, 0, 100) : 0;
            var pctText = pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            if (seek.QuerySelector(".cupri-video-seek-fill") is { } fill) fill.SetAttribute("style", $"width:{pctText}%");
            if (seek.QuerySelector(".cupri-video-seek-thumb") is { } thumb)
                thumb.SetAttribute("style", $"left:{pctText}%;margin-left:-6px"); // centre the 12px thumb
        }
    }
}
