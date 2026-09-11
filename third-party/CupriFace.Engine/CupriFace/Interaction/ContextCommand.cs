namespace CupriFace.Interaction;

/// <summary>
/// A right-click context-menu action on a text field. The engine raises the chosen command
/// (see <c>CupriDocument.ContextRequested</c>); the <b>host</b> performs it — because the
/// clipboard is a platform concern (synchronous on desktop, asynchronous on the web). This is
/// the same host/engine seam the keyboard shortcuts use.
/// </summary>
public enum ContextCommand
{
    Cut,        // copy the selection to the clipboard, then delete it
    Copy,       // copy the selection to the clipboard
    Paste,      // insert the clipboard text at the caret
    SelectAll,  // select all text in the field
}
