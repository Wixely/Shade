namespace CupriFace.Interaction;

/// <summary>
/// A window-level request the engine cannot perform itself (it owns pixels, not the OS window).
/// Raised via <see cref="CupriDocument.WindowCommandRequested"/> — from a
/// <c>data-window-command</c> element (e.g. a video's ⛶ button) — and performed by the host:
/// the desktop windows toggle their OS fullscreen state, the web host calls the browser's
/// Fullscreen API. Same engine→host split as <c>Navigated</c> and clipboard.
/// </summary>
public enum WindowCommand
{
    ToggleFullscreen,
    EnterFullscreen,
    ExitFullscreen,
}

/// <summary>
/// A request to move the OS window by a delta, raised while a drag is live on an element marked
/// <c>data-window-drag</c> — the title bar a frameless window does not have.
///
/// <para>A delta rather than a destination, because the engine knows only its own coordinate space:
/// it reports how far the pointer has travelled from where it was pressed, and the host adds that to
/// wherever the window happens to be. Once the host moves, the pointer is back over the point it
/// grabbed, which is what makes the next delta measure from the same origin again.</para>
///
/// <para>Hosts that do not own a movable window ignore it — a browser page cannot move itself, and
/// an Android activity has no position — exactly as they differ over
/// <see cref="WindowCommand"/>.</para>
/// </summary>
public readonly record struct WindowMove(int Dx, int Dy);
