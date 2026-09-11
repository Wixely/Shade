using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-textfield value="{{Name}}" placeholder="…"&gt;</c> — an editable single-line
/// text field. role=textbox; two-way binds its value; focus/caret/typing are driven by the
/// document's key dispatch.
/// </summary>
public sealed class TextFieldComponent : ComponentBase
{
    public override string Tag => "cupri-textfield";
    public override string DefaultCss => """
        /* min-height reserves one line so the field never collapses when its value renders no
           line box — e.g. a whitespace-only value (the render tree drops whitespace text nodes)
           or an empty field mid-frame. Matches how real form controls keep a fixed height. */
        .cupri-textfield { display:inline-block; min-width:220px; min-height:20px; background:var(--cupri-surface, white);
                           border:2px var(--cupri-border, #cbd2dc); border-radius:8px; padding:9px 12px; font-size:15px;
                           white-space:nowrap; overflow:hidden; } /* single line: a long value scrolls, not wraps */
        .cupri-textfield[data-hover] { border-color:#98a2b3; }
        .cupri-textfield:focus { border-color:var(--cupri-accent,#B87333); }
        .cupri-textfield[data-invalid] { border-color:#d92d20; }
        .cupri-tf-text { color:var(--cupri-text, #1e2430); }
        .cupri-tf-ph { color:var(--cupri-muted, #98a2b3); }
        /* Inline validation message the engine injects after an invalid, visited field. */
        .cupri-field-error { display:block; color:#d92d20; font-size:13px; margin:5px 0 2px; }
        """;

    public override void Expand(IElement el)
    {
        var value = Str(el, "value");
        el.SetAttribute("role", "textbox");
        el.ClassList.Add("cupri-textfield");
        el.InnerHtml = value.Length > 0
            ? $"<span class='cupri-tf-text' data-caret-anchor>{Escape(value)}</span>"
            : $"<span class='cupri-tf-ph' data-caret-anchor>{Escape(Str(el, "placeholder"))}</span>";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
