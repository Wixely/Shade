using System.Linq;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-carousel&gt;&lt;cupri-slide&gt;…&lt;/cupri-slide&gt;…&lt;/cupri-carousel&gt;</c> — a
/// horizontal strip of equal-width panels that scrolls sideways.
///
/// <para>It is a scroll container, not a widget with its own gesture handling: the engine gained a
/// second scrolling axis, so a finger drag, a horizontal wheel, a fling and the overscroll rubber-band
/// all work here for free. Building bespoke drag handling would mean reimplementing every one of them
/// slightly differently.</para>
///
/// <para>Slides are given a definite width, because that is what makes the track overflow its
/// viewport and therefore scroll at all — a strip of intrinsically-sized children would size the
/// track to the viewport and never move. <c>slide-width</c> sets it; <c>peek</c> is the more useful
/// spelling, sizing slides so a sliver of the next one shows, which is what tells a reader there is
/// more to the side without needing a scrollbar to say so.</para>
/// </summary>
public sealed class CarouselComponent : ComponentBase
{
    public override string Tag => "cupri-carousel";

    public override string DefaultCss => """
        .cupri-carousel { display:block; }
        /* The viewport IS the row. A scroll box measures its DIRECT children to find its scrollable
           extent, so slides wrapped in an inner track would leave the track — one child, viewport
           width — as the only thing measured, and nothing would ever scroll. */
        .cupri-carousel-viewport { display:flex; align-items:stretch; overflow:scroll; }
        .cupri-carousel-slide { flex:none; background:var(--cupri-surface, #fff);
                                border:1px var(--cupri-border, #e6e9f0); border-radius:10px;
                                padding:14px; color:var(--cupri-text, #1e2430); box-sizing:border-box; }
        """;

    public override void Expand(IElement el)
    {
        var slides = el.Children.Where(c => c.LocalName == "cupri-slide").ToList();
        var gap = Num(el, "gap", 12);
        var height = Num(el, "height", 0);

        // `peek` wins when both are given: it is the intent ("show a sliver of the next"), while
        // slide-width is the mechanism.
        var peek = Num(el, "peek", 0);
        var slideW = Num(el, "slide-width", 260);

        var sb = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            var w = peek > 0
                // A slide sized so the next one's edge shows past the viewport. Expressed in CSS so it
                // tracks the container's real width — the component cannot know it at expand time.
                ? $"width:calc(100% - {F(peek + gap)}px)"
                : $"width:{F(slideW)}px";
            sb.Append($"<div class='cupri-carousel-slide' role='group' ")
              .Append($"aria-label='{i + 1} of {slides.Count}' ")
              .Append($"style='{w}{(i < slides.Count - 1 ? $";margin-right:{F(gap)}px" : "")}'>")
              .Append(slides[i].InnerHtml)
              .Append("</div>");
        }

        el.SetAttribute("role", "group");
        el.SetAttribute("aria-roledescription", "carousel");
        if (Str(el, "label") is { Length: > 0 } label) el.SetAttribute("aria-label", label);
        el.ClassList.Add("cupri-carousel");

        var vpStyle = height > 0 ? $" style='height:{F(height)}px'" : "";
        el.InnerHtml = $"<div class='cupri-carousel-viewport'{vpStyle}>{sb}</div>";
    }
}

/// <summary>Declares one panel of a <see cref="CarouselComponent"/>. Consumed by its parent — the
/// element renders nothing itself, like <c>cupri-tab</c> and <c>cupri-crumb</c>.</summary>
public sealed class SlideComponent : ComponentBase
{
    public override string Tag => "cupri-slide";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}
