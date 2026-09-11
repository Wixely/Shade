using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-range low="{{From}}" high="{{To}}" min max&gt;</c> — a two-thumb slider for a span:
/// a price filter, a date window, an age bracket. <c>cupri-slider</c> covers one value; this covers
/// the pair, which is the case an app otherwise fakes with two sliders that can cross each other.
///
/// <para>Each thumb is its OWN <c>role="slider"</c>, which is what makes it work rather than merely
/// look right: each is separately focusable, arrow keys nudge whichever holds focus, and a screen
/// reader reads two values instead of one control with a mystery second handle. The track carries
/// <c>data-slider-track</c> so a drag maps across the SCALE and not across the 18px thumb the pointer
/// happened to land on.</para>
///
/// <para>The thumbs cannot cross, and no code enforces that: the low thumb's <c>max</c> is the high
/// value and the high thumb's <c>min</c> is the low value, so the existing clamp in the drag does it.
/// A constraint expressed as data cannot be forgotten by a later edit to the drag path.</para>
/// </summary>
public sealed class RangeComponent : ComponentBase
{
    public override string Tag => "cupri-range";

    public override string DefaultCss => """
        /* Same min-width reasoning as cupri-slider: the content is absolutely positioned, so beside a
           flex:1 label this would otherwise collapse to nothing and be undraggable. */
        .cupri-range { display:block; padding:9px 9px; min-width:140px; }
        .cupri-range-track { position:relative; height:6px; background:#d7dbe3; border-radius:3px; }
        /* The selected span, between the two thumbs. */
        .cupri-range-fill { position:absolute; top:0; height:6px; background:var(--cupri-accent,#B87333);
                            border-radius:3px; left:var(--cupri-low, 0%);
                            width:calc(var(--cupri-high, 100%) - var(--cupri-low, 0%)); }
        .cupri-range-thumb { position:absolute; top:-7px; width:18px; height:18px; background:white;
                             border:2px var(--cupri-accent,#B87333); border-radius:10px; }
        .cupri-range-thumb.low  { left:calc(var(--cupri-low, 0%) - 9px); }
        .cupri-range-thumb.high { left:calc(var(--cupri-high, 100%) - 9px); }
        .cupri-range-thumb[data-focus] { border:2px var(--cupri-text, #1e2430); }
        """;

    public override void Expand(IElement el)
    {
        var min = Num(el, "min", 0);
        var max = Num(el, "max", 100);
        var low = Math.Clamp(Num(el, "low", min), min, max);
        var high = Math.Clamp(Num(el, "high", max), min, max);
        if (high < low) (low, high) = (high, low);

        // The bound paths. The binder rewrites low/high into data-bind-low/high, the same way a
        // single value becomes data-bind-value — see BindingEngine.
        var lowPath = Str(el, "data-bind-low");
        var highPath = Str(el, "data-bind-high");

        el.SetAttribute("role", "group");
        el.SetAttribute("aria-label", Str(el, "label", "Range"));
        el.ClassList.Add("cupri-range");

        el.InnerHtml =
            $"<div class='cupri-range-track' data-slider-track " +
            $"style='--cupri-low:{F(Percent(low, min, max))}%;--cupri-high:{F(Percent(high, min, max))}%'>" +
            $"<div class='cupri-range-fill'></div>" +
            Thumb("low", lowPath, low, min, max, clampMax: high, peer: highPath) +
            Thumb("high", highPath, high, min, max, clampMin: low, peer: lowPath) +
            $"</div>";
    }

    /// <summary>One thumb: a slider in its own right.
    ///
    /// <para><c>min</c>/<c>max</c> are the SCALE — the whole range, because that is what the track's
    /// width represents and the pointer must land where you point. The bound that stops the thumbs
    /// crossing is separate (<c>data-clamp-*</c>): folding it into min/max instead re-scaled the drag,
    /// so a drag to 60% of the track landed at 60% of whatever was left. ARIA reports the clamp, since
    /// what a screen reader wants is where this thumb may actually go.</para></summary>
    private static string Thumb(string which, string path, double value, double min, double max,
                                double? clampMin = null, double? clampMax = null, string peer = "") =>
        $"<div class='cupri-range-thumb {which}' role='slider' tabindex='0'" +
        (path.Length > 0 ? $" data-bind-value='{path}'" : "") +
        // Which side this thumb is, and where its neighbour's value lives. The engine needs both to
        // handle a press landing where the two coincide: only the thumb painted last is hit-testable
        // there, so without this the other could never be dragged off it again.
        $" data-drag-side='{which}'" +
        (peer.Length > 0 ? $" data-peer-path='{peer}'" : "") +
        $" min='{F(min)}' max='{F(max)}'" +
        (clampMin is { } lo ? $" data-clamp-min='{F(lo)}'" : "") +
        (clampMax is { } hi ? $" data-clamp-max='{F(hi)}'" : "") +
        $" aria-valuemin='{F(clampMin ?? min)}' aria-valuemax='{F(clampMax ?? max)}'" +
        $" aria-valuenow='{F(value)}' aria-label='{which}'></div>";
}
