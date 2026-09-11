using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-number value="{{Count}}" min="0" max="100" step="1"&gt;</c> — an editable
/// numeric field. role=spinbutton; two-way binds its value; supports typed digit entry
/// (filtered to numeric) plus up/down steppers. Bound to an int/double on the model.
/// </summary>
public sealed class NumberFieldComponent : ComponentBase
{
    public override string Tag => "cupri-number";
    public override string DefaultCss => """
        .cupri-number { display:inline-flex; align-items:stretch; min-width:120px; background:var(--cupri-surface, white);
                        border:2px var(--cupri-border, #cbd2dc); border-radius:8px; font-size:15px; }
        .cupri-number[data-hover] { border-color:#98a2b3; }
        .cupri-number:focus { border-color:var(--cupri-accent,#B87333); }
        .cupri-number[data-invalid] { border-color:#d92d20; }
        .cupri-num-text { flex:1; padding:9px 12px; color:var(--cupri-text, #1e2430); }
        .cupri-num-steps { display:flex; flex-direction:column; width:26px; border-left:1px var(--cupri-border, #cbd2dc); }
        .cupri-num-step { flex:1; display:flex; align-items:center; justify-content:center;
                          color:var(--cupri-muted, #667085); }
        .cupri-num-step[data-hover] { background:var(--cupri-hover, #f0f2f5); color:var(--cupri-accent,#B87333); }
        .cupri-num-icon { width:14px; height:14px; }
        """;

    public override void Expand(IElement el)
    {
        var value = Str(el, "value", "0");
        el.SetAttribute("role", "spinbutton");
        el.SetAttribute("data-numeric", "");
        el.SetAttribute("aria-valuenow", value);
        // Carry min/max/step onto the field so the stepper handler can clamp.
        if (el.GetAttribute("min") is { Length: > 0 } min) { el.SetAttribute("data-min", min); el.SetAttribute("aria-valuemin", min); }
        if (el.GetAttribute("max") is { Length: > 0 } max) { el.SetAttribute("data-max", max); el.SetAttribute("aria-valuemax", max); }
        if (el.GetAttribute("step") is { Length: > 0 } step) el.SetAttribute("data-step", step);
        el.ClassList.Add("cupri-number");
        el.InnerHtml =
            $"<span class='cupri-num-text' data-caret-anchor>{Escape(value)}</span>" +
            "<div class='cupri-num-steps'>" +
                $"<div class='cupri-num-step' data-cupri-step='1'>{IconMarkup("chevron-up", 14, "cupri-num-icon")}</div>" +
                $"<div class='cupri-num-step' data-cupri-step='-1'>{IconMarkup("chevron-down", 14, "cupri-num-icon")}</div>" +
            "</div>";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
