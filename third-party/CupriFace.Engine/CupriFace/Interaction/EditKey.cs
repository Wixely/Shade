namespace CupriFace.Interaction;

/// <summary>Non-printable keys delivered to the document: text editing plus keyboard focus.</summary>
public enum EditKey
{
    None,
    Backspace,
    Delete,
    Left,
    Right,
    Home,
    End,
    Enter,
    Up,         // arrow up (group / slider nav)
    Down,       // arrow down (group / slider nav)
    Tab,        // move keyboard focus to the next control
    ShiftTab,   // move keyboard focus to the previous control
    Space,      // activate the focused (non-text) control
    Escape,     // close the top-most open overlay (or blur a field)
    SelectAll,  // Ctrl+A — select all text in the focused field
}

/// <summary>Modifier keys held during a key/pointer event (for text selection + shortcuts).</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Shift = 1,  // extend the selection instead of collapsing it
    Ctrl = 2,   // word-wise movement/delete (Cmd on macOS maps here too)
}
