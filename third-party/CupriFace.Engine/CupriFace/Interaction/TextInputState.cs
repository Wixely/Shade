namespace CupriFace.Interaction;

/// <summary>
/// The focused text field as an IME layer sees it — identity, kind, content, selection, and where
/// the caret is on screen. Hosts use it to show/hide the soft keyboard, fill the platform's editor
/// info (numeric vs text vs password vs multiline), answer an input method's synchronous questions,
/// and position candidate windows.
///
/// A value type on purpose: <see cref="CupriDocument.GetTextInputState"/> hands out a snapshot
/// that is safe to publish across threads (the Android host reads it from the UI thread while the
/// document lives on the GL thread). <c>default</c> means "nothing focused".
/// </summary>
/// <param name="Focused">A text-editing field has focus.</param>
/// <param name="Role">The field's ARIA role ("textbox" / "spinbutton"), when layout has it.</param>
/// <param name="Numeric">The field takes numbers (<c>data-numeric</c>) — numeric keypad.</param>
/// <param name="Multiline">The field takes hard line breaks (<c>data-multiline</c>).</param>
/// <param name="Masked">A password field (<c>data-mask</c>) — no suggestions, no learning.</param>
/// <param name="Value">The current EDIT BUFFER — permissive, possibly invalid mid-edit, and
/// including any in-flight composition text.</param>
/// <param name="SelStart">Selection start (UTF-16 units into <paramref name="Value"/>).</param>
/// <param name="SelEnd">Selection end; equal to start when the selection is empty.</param>
/// <param name="Composing">An IME composition (preedit) is in progress.</param>
/// <param name="CaretRect">The caret's rectangle in LOGICAL px, or null when layout is dirty —
/// poll again after the next frame.</param>
public readonly record struct TextInputState(
    bool Focused,
    string? Role,
    bool Numeric,
    bool Multiline,
    bool Masked,
    string Value,
    int SelStart,
    int SelEnd,
    bool Composing,
    (float X, float Y, float W, float H)? CaretRect,

    /// <summary>The web platform's <c>inputmode</c>, authored on the field: text, decimal, numeric,
    /// tel, search, email, url. One attribute, consumed by Android's EditorInfo and by the web
    /// host's own inputmode — the vocabulary already exists, so inventing another would only mean
    /// authors learning two.</summary>
    string InputMode = "",

    /// <summary>The web platform's <c>enterkeyhint</c>: enter, done, go, next, previous, search,
    /// send. What the keyboard's action key should say and mean.</summary>
    string EnterKeyHint = "",

    /// <summary>The field's placeholder. A keyboard shows it while editing in extract mode, where
    /// the app's own rendering is not on screen at all.</summary>
    string Placeholder = "");
