using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-slider min max value&gt;</c> — a themed, accessible slider. Expands to a
/// track/fill/thumb and exposes role=slider with aria-valuemin/max/now.
/// </summary>
public sealed class SliderComponent : ComponentBase
{
    public override string Tag => "cupri-slider";

    // The live position is published as the inherited custom property `--cupri-fill` (a percentage);
    // fill/thumb geometry reads it from CSS, so every part stays fully overridable via a stylesheet
    // while the value itself stays data-driven. Thumb is 18px wide → offset by half to centre it.
    public override string DefaultCss => """
        /* min-width is load-bearing: as a flex item beside a `flex:1` label the slider's base size
           is its content, and its content is absolutely positioned — so it collapsed to the width
           of the thumb, the track vanished, and the control looked stuck at the far right. A
           slider narrower than this cannot be dragged meaningfully anyway. */
        .cupri-slider { display:block; padding:9px 9px; min-width:120px; }
        .cupri-slider-track { position:relative; height:6px; background:#d7dbe3; border-radius:3px; }
        .cupri-slider-fill { position:absolute; top:0; left:0; height:6px; background:var(--cupri-accent,#B87333);
                             border-radius:3px; width:var(--cupri-fill, 0%); }
        .cupri-slider-thumb { position:absolute; top:-7px; width:18px; height:18px; background:white;
                              border:2px var(--cupri-accent,#B87333); border-radius:10px; left:calc(var(--cupri-fill, 0%) - 9px); }
        """;

    public override void Expand(IElement el)
    {
        var min = Num(el, "min", 0);
        var max = Num(el, "max", 100);
        var value = Num(el, "value", min);
        var pct = Percent(value, min, max);

        el.SetAttribute("role", "slider");
        el.SetAttribute("aria-valuemin", F(min));
        el.SetAttribute("aria-valuemax", F(max));
        el.SetAttribute("aria-valuenow", F(value));
        el.ClassList.Add("cupri-slider");

        // Publish the position on the track; fill + thumb inherit --cupri-fill and read it in CSS.
        el.InnerHtml =
            $"<div class='cupri-slider-track' style='--cupri-fill:{F(pct)}%'>" +
            $"<div class='cupri-slider-fill'></div>" +
            $"<div class='cupri-slider-thumb'></div>" +
            $"</div>";
    }
}
