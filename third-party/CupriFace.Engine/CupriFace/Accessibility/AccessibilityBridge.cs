namespace CupriFace.Accessibility;

/// <summary>
/// Bridges the platform-neutral <see cref="AccessibilityNode"/> tree to a native
/// assistive-technology API. One implementation per platform (DESIGN.md §5):
///   Windows → UI Automation (<c>CupriFace.Shell</c>'s <c>UiaBridge</c> — a real WM_GETOBJECT
///   fragment provider, not this interface, because it needs the window handle and the host's
///   UI-thread tick) · Linux → AT-SPI (D-Bus), not started · macOS → NSAccessibility, not
///   started · web → hidden DOM overlay (<see cref="AriaHtml"/>).
/// </summary>
public interface IAccessibilityBridge
{
    /// <summary>Publish/refresh the semantics tree to the platform AT layer.</summary>
    void Update(AccessibilityNode root);
}
