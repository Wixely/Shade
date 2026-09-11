using System.Linq;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-segmented value="{{View}}"&gt;&lt;cupri-segment value="grid"&gt;Grid&lt;/cupri-segment&gt;…</c>
/// — a connected button bar bound to a value (radios rendered as a segmented control). Clicking a
/// segment writes its value; the matching one is active.
/// </summary>
public sealed class SegmentedComponent : ComponentBase
{
    public override string Tag => "cupri-segmented";
    public override string DefaultCss => """
        .cupri-segmented { display:inline-flex; background:var(--cupri-hover, #eef1f5); border-radius:9px; padding:3px; }
        .cupri-seg { padding:7px 14px; border-radius:7px; font-size:14px; color:var(--cupri-muted, #4a5262); }
        .cupri-seg[data-hover]:not(.active) { color:var(--cupri-text, #1e2430); }
        .cupri-seg.active { background:var(--cupri-surface, white); color:var(--cupri-text, #1e2430); font-weight:bold; }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var value = Str(el, "value");
        var segments = el.Children.Where(c => c.LocalName == "cupri-segment")
            .Select(c => (Value: Str(c, "value"), Label: c.TextContent.Trim())).ToList();
        if (value.Length == 0 && segments.Count > 0) value = segments[0].Value;

        el.SetAttribute("role", "radiogroup");
        el.ClassList.Add("cupri-segmented");

        var sb = new StringBuilder();
        foreach (var (v, label) in segments)
        {
            var active = v == value;
            sb.Append($"<div class='cupri-seg{(active ? " active" : "")}' role='radio' aria-checked='{(active ? "true" : "false")}'")
              .Append(path.Length > 0 ? $" data-set-path='{path}' data-set-value='{Attr(v)}'" : "")
              .Append($">{label}</div>");
        }
        el.InnerHtml = sb.ToString();
    }

    private static string Attr(string s) => s.Replace("'", "&#39;");
}

/// <summary>A choice inside <c>&lt;cupri-segmented&gt;</c>; the parent consumes it (see SegmentedComponent).</summary>
public sealed class SegmentComponent : ComponentBase
{
    public override string Tag => "cupri-segment";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { } // consumed by the parent
}
