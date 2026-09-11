using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-progress value max&gt;</c> — a themed progress bar. role=progressbar.
/// </summary>
public sealed class ProgressComponent : ComponentBase
{
    public override string Tag => "cupri-progress";

    // Fill width reads the live position from the custom property `--cupri-fill`, so the fill stays
    // fully styleable via a stylesheet while the value itself stays data-driven.
    public override string DefaultCss => """
        .cupri-progress { display:block; height:12px; background:#e2e6ec; border-radius:6px; }
        .cupri-progress-fill { height:12px; background:var(--cupri-accent,#B87333); border-radius:6px; width:var(--cupri-fill, 0%); }
        """;

    public override void Expand(IElement el)
    {
        var max = Num(el, "max", 100);
        var value = Num(el, "value", 0);
        var pct = Percent(value, 0, max);

        el.SetAttribute("role", "progressbar");
        el.SetAttribute("aria-valuemin", "0");
        el.SetAttribute("aria-valuemax", F(max));
        el.SetAttribute("aria-valuenow", F(value));
        el.ClassList.Add("cupri-progress");

        el.InnerHtml = $"<div class='cupri-progress-fill' style='--cupri-fill:{F(pct)}%'></div>";
    }
}
