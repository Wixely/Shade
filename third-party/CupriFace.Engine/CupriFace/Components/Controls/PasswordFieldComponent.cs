using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-password value="{{Pw}}" reveal="{{Show}}"&gt;</c> — a masked text field. The bound
/// value holds the plaintext; the field paints bullets (the engine masks it via <c>data-mask</c> while
/// editing, and this component masks it while unfocused). Editing works like <c>&lt;cupri-textfield&gt;</c>.
/// If <c>reveal</c> is two-way bound, a trailing eye toggles between masked and plaintext.
/// </summary>
public sealed class PasswordFieldComponent : ComponentBase
{
    public override string Tag => "cupri-password";
    public override string DefaultCss => """
        .cupri-pw { display:inline-flex; align-items:center; gap:8px; min-width:220px;
                    background:var(--cupri-surface, white); border:2px var(--cupri-border, #cbd2dc);
                    border-radius:8px; padding:8px 12px; }
        .cupri-pw[data-hover] { border-color:#98a2b3; }
        .cupri-pw-field { flex:1; min-height:20px; font-size:15px; white-space:nowrap; overflow:hidden; }
        .cupri-pw-eye { color:var(--cupri-muted, #98a2b3); display:inline-flex; }
        .cupri-pw-eye[data-hover] { color:var(--cupri-text, #1e2430); }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var revealPath = el.GetAttribute("data-bind-reveal"); // null unless reveal is two-way bound
        var value = Str(el, "value");
        var revealed = revealPath is not null && Flag(el, "reveal");
        var placeholder = Str(el, "placeholder", "Password");

        el.RemoveAttribute("data-bind-value"); // the inner field owns the binding (avoid a duplicate)
        el.ClassList.Add("cupri-pw");

        // Unfocused display: placeholder when empty, else bullets (or the plaintext when revealed).
        // While focused, the engine overwrites this text from the edit buffer, masking it when data-mask
        // is present — so masking here and there stay in lock-step.
        string display = value.Length == 0
            ? $"<span class='cupri-tf-ph' data-caret-anchor>{Escape(placeholder)}</span>"
            : $"<span class='cupri-tf-text' data-caret-anchor>{(revealed ? Escape(value) : new string('•', value.Length))}</span>";
        var maskAttr = revealed ? "" : " data-mask";
        var field = $"<div class='cupri-pw-field' role='textbox'{(path.Length > 0 ? $" data-bind-value='{path}'" : "")}{maskAttr}>{display}</div>";

        var eye = "";
        if (revealPath is not null)
            eye = $"<div class='cupri-pw-eye' role='button' aria-label='{(revealed ? "Hide" : "Show")} password'"
                + $" data-set-path='{revealPath}' data-set-value='{(revealed ? "false" : "true")}'>{IconMarkup(revealed ? "eye-off" : "eye", 18)}</div>";

        el.InnerHtml = $"{field}{eye}";
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
