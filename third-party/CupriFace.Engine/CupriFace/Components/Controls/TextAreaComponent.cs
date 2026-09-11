using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-textarea value="{{Notes}}" placeholder="…"&gt;</c> — a multi-line editable text
/// field. role=textbox + aria-multiline; each buffer line renders as its own block so hard
/// newlines (Enter) stack. The document's key dispatch inserts newlines and the caret is
/// positioned per line (see CupriDocument.AppendCaret / the data-multiline buffer patch).
///
/// <para><c>submit-on-enter</c> makes it a chat composer: Enter submits and Shift+Enter starts a new
/// line, answered by <c>doc.OnSubmit("data-…", …)</c>. It also labels the on-screen keyboard's action
/// key "send" unless an <c>enterkeyhint</c> was authored. Per-field on purpose — a global Enter
/// shortcut would eat newlines in every other textarea on the page (#90).</para>
/// </summary>
public sealed class TextAreaComponent : ComponentBase
{
    public override string Tag => "cupri-textarea";
    public override string DefaultCss => """
        .cupri-textarea { display:block; min-width:260px; min-height:78px; overflow:auto;
                          background:var(--cupri-surface, white);
                          border:2px var(--cupri-border, #cbd2dc); border-radius:8px; padding:10px 12px; font-size:15px; }
        .cupri-textarea[data-hover] { border-color:#98a2b3; }
        .cupri-textarea:focus { border-color:var(--cupri-accent,#B87333); }
        .cupri-textarea[data-invalid] { border-color:#d92d20; }
        .cupri-ta-body { color:var(--cupri-text, #1e2430); }
        .cupri-ta-line { display:block; }
        .cupri-ta-ph { color:var(--cupri-muted, #98a2b3); }
        """;

    public override void Expand(IElement el)
    {
        var value = Str(el, "value");
        el.SetAttribute("role", "textbox");
        el.SetAttribute("aria-multiline", "true");
        el.SetAttribute("data-multiline", "");
        // Opt-in "follow the tail": when already scrolled to the bottom, new content keeps it pinned
        // there (logging). The engine reads data-follow-tail on rebuild (see CupriDocument.Rebuild).
        if (Flag(el, "follow-tail")) el.SetAttribute("data-follow-tail", "");
        // Opt-in "Enter sends, Shift+Enter starts a new line" — the chat-composer idiom. Local to this
        // field so every other textarea keeps a plain Enter; the app answers it with doc.OnSubmit (#90).
        if (Flag(el, "submit-on-enter"))
        {
            el.SetAttribute("data-submit-on-enter", "");
            // One authored attribute should drive the on-screen keyboard's action key too, so a phone
            // agrees with the desktop instead of offering a newline key that sends. An explicit
            // enterkeyhint still wins — this only fills in the one the behaviour implies.
            if (!el.HasAttribute("enterkeyhint")) el.SetAttribute("enterkeyhint", "send");
        }
        el.ClassList.Add("cupri-textarea");
        var inner = value.Length > 0
            ? RenderLines(value)
            : $"<div class='cupri-ta-line cupri-ta-ph'>{PlaceholderLine(Str(el, "placeholder"))}</div>";
        el.InnerHtml = $"<div class='cupri-ta-body' data-caret-anchor>{inner}</div>";
    }

    /// <summary>Render each newline-separated line of <paramref name="text"/> as its own block.
    /// Empty lines get a zero-width space so they still reserve a line box (no collapse).</summary>
    private const string Zwsp = "​"; // zero-width space: reserves a line box for an empty line

    public static string RenderLines(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Length > 0 && raw[^1] == '\r' ? raw[..^1] : raw; // tolerate stray CRLF '\r'
            sb.Append("<div class='cupri-ta-line'>")
              .Append(line.Length == 0 ? Zwsp : Escape(line))
              .Append("</div>");
        }
        return sb.ToString();
    }

    private static string PlaceholderLine(string s) => s.Length == 0 ? Zwsp : Escape(s);

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
