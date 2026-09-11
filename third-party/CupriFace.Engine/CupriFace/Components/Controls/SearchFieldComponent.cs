using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-search value="{{Query}}" placeholder="…"&gt;</c> — a single-line text field with a
/// leading search icon and a trailing clear (×) button that appears once there's text. Editing works
/// like <c>&lt;cupri-textfield&gt;</c>; it reuses the <c>.cupri-tf-*</c> text classes.
/// </summary>
public sealed class SearchFieldComponent : ComponentBase
{
    public override string Tag => "cupri-search";
    public override string DefaultCss => """
        .cupri-search { display:inline-flex; align-items:center; gap:8px; min-width:220px;
                        background:var(--cupri-surface, white); border:2px var(--cupri-border, #cbd2dc);
                        border-radius:8px; padding:8px 12px; }
        .cupri-search[data-hover] { border-color:#98a2b3; }
        .cupri-search-icon { color:var(--cupri-muted, #98a2b3); display:inline-flex; }
        .cupri-search-field { flex:1; min-height:20px; font-size:15px; white-space:nowrap; overflow:hidden; }
        .cupri-search-clear { color:var(--cupri-muted, #98a2b3); display:inline-flex; }
        .cupri-search-clear[data-hover] { color:var(--cupri-text, #1e2430); }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var value = Str(el, "value");
        var placeholder = Str(el, "placeholder", "Search…");

        el.RemoveAttribute("data-bind-value"); // the inner field owns the binding (avoid a duplicate)
        el.ClassList.Add("cupri-search");
        var display = value.Length > 0
            ? $"<span class='cupri-tf-text' data-caret-anchor>{Escape(value)}</span>"
            : $"<span class='cupri-tf-ph' data-caret-anchor>{Escape(placeholder)}</span>";
        var field = $"<div class='cupri-search-field' role='textbox'{(path.Length > 0 ? $" data-bind-value='{path}'" : "")}>{display}</div>";
        var clear = value.Length > 0 && path.Length > 0
            ? $"<div class='cupri-search-clear' role='button' aria-label='Clear' data-set-path='{path}' data-set-value=''>{IconMarkup("close", 16)}</div>"
            : "";
        el.InnerHtml = $"<div class='cupri-search-icon'>{IconMarkup("search", 18)}</div>{field}{clear}";
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
