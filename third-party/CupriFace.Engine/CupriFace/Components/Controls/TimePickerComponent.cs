using System;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-timepicker value="{{Time}}" open="{{Open}}"&gt;</c> — a time field bound to a
/// <c>HH:mm</c> (24-hour) string. The trigger shows the time; clicking it opens a popup with two
/// scrollable columns (hours 00–23, minutes 00–59). Picking an hour or minute updates that part and
/// keeps the popup open (<c>data-set-keep</c>) so you can pick both; the trigger toggles it closed.
/// </summary>
public sealed class TimePickerComponent : ComponentBase
{
    public override string Tag => "cupri-timepicker";
    public override string DefaultCss => """
        .cupri-tp { display:inline-block; }
        .cupri-tp-trigger { display:inline-flex; align-items:center; justify-content:space-between; gap:10px;
                            min-width:120px; padding:9px 12px; background:var(--cupri-surface, white);
                            border:2px var(--cupri-border, #cbd2dc); border-radius:8px;
                            color:var(--cupri-text, #1e2430); font-size:15px; }
        .cupri-tp-trigger[data-hover] { border-color:#98a2b3; }
        .cupri-tp-ph { color:var(--cupri-muted, #98a2b3); }
        .cupri-tp-popup { position:fixed; display:flex; gap:6px; background:var(--cupri-surface, white);
                          border-radius:10px; padding:8px; z-index:30; border:1px var(--cupri-border, #e6e9f0);
                          box-shadow:0 10px 28px #00000026; }
        .cupri-tp-col { height:180px; overflow:scroll; width:56px; }
        .cupri-tp-opt { text-align:center; padding:7px 0; border-radius:6px; font-size:14px; color:var(--cupri-text, #1e2430); }
        .cupri-tp-opt[data-hover] { background:var(--cupri-hover, #eef1f5); }
        .cupri-tp-opt.selected { background:var(--cupri-accent,#B87333); color:white; font-weight:bold; }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var value = Str(el, "value");
        var open = Flag(el, "open");
        var id = NextId();

        int hh = 0, mm = 0;
        var parts = value.Split(':');
        var hasValue = parts.Length == 2 && int.TryParse(parts[0], out hh) && int.TryParse(parts[1], out mm);
        hh = Math.Clamp(hh, 0, 23);
        mm = Math.Clamp(mm, 0, 59);

        var body = new StringBuilder();
        if (open)
        {
            body.Append($"<div class='cupri-tp-popup' role='dialog' data-focus-scope data-cupri-anchor='{id}' data-cupri-placement='bottom'>");
            Column(body, "hours", 24, hh, h => $"{h:D2}:{mm:D2}", path);   // pick hour → keep minute
            Column(body, "minutes", 60, mm, m => $"{hh:D2}:{m:D2}", path); // pick minute → keep hour
            body.Append("</div>");
        }

        var label = hasValue
            ? $"<span>{hh:D2}:{mm:D2}</span>"
            : $"<span class='cupri-tp-ph'>{Escape(Str(el, "placeholder", "--:--"))}</span>";

        el.SetAttribute("role", "combobox");
        el.SetAttribute("aria-expanded", open ? "true" : "false");
        el.ClassList.Add("cupri-tp");
        el.InnerHtml =
            $"<div class='cupri-tp-trigger' id='{id}' data-cupri-toggle=\"{id}\">{label}{IconMarkup("clock", 16)}</div>" +
            body;
    }

    private static void Column(StringBuilder sb, string kind, int count, int selected, Func<int, string> valueFor, string path)
    {
        sb.Append($"<div class='cupri-tp-col' role='listbox' aria-label='{kind}'>");
        for (var i = 0; i < count; i++)
        {
            var isSel = i == selected;
            sb.Append($"<div class='cupri-tp-opt{(isSel ? " selected" : "")}' role='option' aria-selected='{(isSel ? "true" : "false")}'")
              .Append(path.Length > 0 ? $" data-set-path='{path}' data-set-value='{valueFor(i)}' data-set-keep" : "")
              .Append($">{i:D2}</div>");
        }
        sb.Append("</div>");
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
