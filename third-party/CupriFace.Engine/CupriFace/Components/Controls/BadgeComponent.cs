using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-badge&gt;</c> — a small themed pill that keeps its child label.
/// </summary>
public sealed class BadgeComponent : ComponentBase
{
    public override string Tag => "cupri-badge";

    public override string DefaultCss => """
        .cupri-badge { display:inline-block; padding:4px 10px; border-radius:11px;
                       background:var(--cupri-hover, #eef1f5); color:var(--cupri-text, #48505c); font-size:13px; font-weight:bold; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-badge");
    }
}
