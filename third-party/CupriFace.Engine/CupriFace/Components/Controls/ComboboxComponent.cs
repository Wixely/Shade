using System.Linq;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-combobox value="{{City}}" placeholder="…"&gt;&lt;cupri-option value="London"&gt;London&lt;/cupri-option&gt;…</c>
/// — a typeahead: an editable single-line field with a suggestion list that filters as you type. The
/// dropdown appears while the field is focused (a <c>:focus</c> rule — the list is always in the DOM,
/// just hidden), options are filtered by the current text (case-insensitive substring), and clicking a
/// suggestion writes its value. Free-text: whatever you type is the value, so it also allows entries
/// that aren't in the list.
/// </summary>
public sealed class ComboboxComponent : ComponentBase
{
    public override string Tag => "cupri-combobox";
    public override string DefaultCss => """
        .cupri-combobox { display:inline-block; }
        .cupri-cb-input { display:inline-block; min-width:200px; min-height:20px; background:var(--cupri-surface, white);
                          border:2px var(--cupri-border, #cbd2dc); border-radius:8px; padding:9px 12px; font-size:15px;
                          white-space:nowrap; overflow:hidden; }
        .cupri-cb-input[data-hover] { border-color:#98a2b3; }
        .cupri-cb-input:focus { border-color:var(--cupri-accent,#B87333); }
        .cupri-cb-popup { position:fixed; background:var(--cupri-surface, white); border-radius:10px; padding:6px;
                          z-index:30; border:1px var(--cupri-border, #e6e9f0); min-width:200px; display:none;
                          box-shadow:0 10px 28px #00000026; }
        .cupri-cb-input:focus ~ .cupri-cb-popup { display:block; } /* reveal while the field is focused */
        .cupri-cb-option { display:block; padding:9px 12px; border-radius:6px; color:var(--cupri-text, #1e2430); font-size:14px; }
        .cupri-cb-option[data-hover], .cupri-cb-option[data-highlight] { background:var(--cupri-hover, #eef1f5); }
        .cupri-cb-option.selected { color:var(--cupri-accent,#B87333); font-weight:bold; }
        .cupri-cb-empty { padding:9px 12px; color:#98a2b3; font-size:14px; }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var value = Str(el, "value");
        var placeholder = Str(el, "placeholder");
        var id = NextId();

        var options = el.Children.Where(c => c.LocalName == "cupri-option")
            .Select(c => (Value: Str(c, "value"), Label: c.TextContent.Trim())).ToList();

        // Filter suggestions by the current text (empty text → all).
        var q = value.Trim();
        var matches = q.Length == 0
            ? options
            : options.Where(o => o.Label.Contains(q, System.StringComparison.OrdinalIgnoreCase)
                              || o.Value.Contains(q, System.StringComparison.OrdinalIgnoreCase)).ToList();

        var list = new StringBuilder();
        list.Append($"<div class='cupri-cb-popup' role='listbox' data-cupri-anchor='{id}' data-cupri-placement='bottom'>");
        if (matches.Count == 0)
            list.Append("<div class='cupri-cb-empty'>No matches</div>");
        else
            foreach (var (ov, ol) in matches)
            {
                var isSel = ov == value;
                list.Append($"<div class='cupri-cb-option{(isSel ? " selected" : "")}' role='option' aria-selected='{(isSel ? "true" : "false")}'")
                    .Append(path.Length > 0 ? $" data-set-path='{path}' data-set-value='{Attr(ov)}'" : "")
                    .Append($">{Escape(ol)}</div>");
            }
        list.Append("</div>");

        // Editable single-line field (mirrors cupri-textfield; reuses its .cupri-tf-* text classes).
        var display = value.Length > 0
            ? $"<span class='cupri-tf-text' data-caret-anchor>{Escape(value)}</span>"
            : $"<span class='cupri-tf-ph' data-caret-anchor>{Escape(placeholder)}</span>";
        var input = $"<div class='cupri-cb-input' role='textbox' id='{id}' data-listbox"
                  + (path.Length > 0 ? $" data-bind-value='{path}'" : "")
                  + $">{display}</div>";

        el.RemoveAttribute("data-bind-value"); // the inner input owns the binding now (avoid a duplicate)
        el.ClassList.Add("cupri-combobox");
        el.SetAttribute("role", "combobox");
        el.InnerHtml = input + list.ToString();
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Attr(string s) => s.Replace("'", "&#39;");
}
