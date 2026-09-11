using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary><c>&lt;cupri-chip closable&gt;</c> — a small pill; keeps its label, optional close icon.</summary>
public sealed class ChipComponent : ComponentBase
{
    public override string Tag => "cupri-chip";
    public override string DefaultCss => """
        .cupri-chip { display:inline-flex; align-items:center; gap:6px; background:var(--cupri-hover, #eef1f5);
                      color:var(--cupri-text, #48505c); padding:5px 12px; border-radius:14px; font-size:13px; font-weight:bold; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-chip");
        if (Flag(el, "closable")) el.InnerHtml += IconMarkup("close", 14, "chip-x");
    }
}

/// <summary><c>&lt;cupri-avatar initials size&gt;</c> — a circular initials badge.</summary>
public sealed class AvatarComponent : ComponentBase
{
    public override string Tag => "cupri-avatar";
    public override string DefaultCss => """
        .cupri-avatar { display:inline-flex; align-items:center; justify-content:center; width:40px; height:40px;
                        border-radius:20px; background:var(--cupri-accent,#B87333); color:white; font-weight:bold; font-size:15px; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-avatar");
        el.InnerHtml = Str(el, "initials");
    }
}

/// <summary><c>&lt;cupri-card&gt;</c> — a padded, rounded surface; keeps its children.</summary>
public sealed class CardComponent : ComponentBase
{
    public override string Tag => "cupri-card";
    public override string DefaultCss => """
        .cupri-card { display:block; background:var(--cupri-surface, white); border-radius:12px;
                      padding:18px; border:1px var(--cupri-border, #e6e9f0);
                      box-shadow:0 1px 2px #0000001a, 0 4px 12px #00000014; }
        """;

    public override void Expand(IElement el) => el.ClassList.Add("cupri-card");
}

/// <summary><c>&lt;cupri-divider&gt;</c> — a horizontal rule.</summary>
public sealed class DividerComponent : ComponentBase
{
    public override string Tag => "cupri-divider";
    public override string DefaultCss => ".cupri-divider { display:block; height:1px; background:var(--cupri-border, #e0e4ea); margin:10px 0; }";

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "separator");
        el.ClassList.Add("cupri-divider");
    }
}

/// <summary><c>&lt;cupri-stat label value&gt;</c> — a metric with a caption.</summary>
public sealed class StatComponent : ComponentBase
{
    public override string Tag => "cupri-stat";
    public override string DefaultCss => """
        .cupri-stat { display:block; }
        .cupri-stat .n { font-size:26px; font-weight:bold; color:var(--cupri-text, #1e2430); }
        .cupri-stat .l { color:var(--cupri-muted, #8b93a7); font-size:13px; margin-top:2px; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-stat");
        el.InnerHtml = $"<div class='n'>{Str(el, "value")}</div><div class='l'>{Str(el, "label")}</div>";
    }
}
