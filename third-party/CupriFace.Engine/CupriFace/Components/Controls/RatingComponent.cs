using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-rating value="{{Score}}" max="5"&gt;</c> — a star rating. Clicking (or keyboard-activating)
/// the Nth star writes N to the bound value; stars up to the value are filled, the rest are muted.
/// </summary>
public sealed class RatingComponent : ComponentBase
{
    public override string Tag => "cupri-rating";
    public override string DefaultCss => """
        .cupri-rating { display:inline-flex; gap:2px; }
        .cupri-rating-star { color:#f5b301; }
        .cupri-rating-star[data-hover] { color:#e0a800; }
        .cupri-rating-empty { color:var(--cupri-border, #cbd2dc); }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var value = (int)Num(el, "value", 0);
        var max = (int)Num(el, "max", 5);

        el.SetAttribute("role", "slider"); // a value in [0..max]
        el.SetAttribute("aria-valuenow", value.ToString());
        el.SetAttribute("aria-valuemin", "0");
        el.SetAttribute("aria-valuemax", max.ToString());
        el.ClassList.Add("cupri-rating");

        var sb = new StringBuilder();
        for (var i = 1; i <= max; i++)
        {
            var filled = i <= value;
            sb.Append($"<div class='cupri-rating-star{(filled ? "" : " cupri-rating-empty")}' role='button' aria-label='{i}'")
              .Append(path.Length > 0 ? $" data-set-path='{path}' data-set-value='{i}'" : "")
              .Append($">{IconMarkup("star", 22)}</div>");
        }
        el.InnerHtml = sb.ToString();
    }
}
