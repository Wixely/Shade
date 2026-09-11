using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary><c>&lt;cupri-alert type&gt;</c> — a coloured inline banner with an icon; keeps its message.</summary>
public sealed class AlertComponent : ComponentBase
{
    public override string Tag => "cupri-alert";
    public override string DefaultCss => """
        .cupri-alert { display:flex; align-items:center; gap:10px; padding:12px 14px; border-radius:8px; font-size:14px; }
        .cupri-alert.info { background:#e8f0fe; color:#1a56db; }
        .cupri-alert.success { background:#e6f4ea; color:#1e7e34; }
        .cupri-alert.warning { background:#fef7e0; color:#a16207; }
        .cupri-alert.error { background:#fde8e8; color:#c81e1e; }
        .cupri-alert .alert-body { flex:1; }
        """;

    public override void Expand(IElement el)
    {
        var type = Str(el, "type", "info");
        var icon = type switch { "success" => "check", "warning" => "warning", "error" => "error", _ => "info" };
        el.SetAttribute("role", "alert");
        el.ClassList.Add("cupri-alert");
        el.ClassList.Add(type);
        el.InnerHtml = IconMarkup(icon, 20, "alert-icon") + $"<div class='alert-body'>{el.InnerHtml}</div>";
    }
}

/// <summary><c>&lt;cupri-spinner&gt;</c> — a rotating loading indicator (an icon spun via @keyframes).</summary>
public sealed class SpinnerComponent : ComponentBase
{
    public override string Tag => "cupri-spinner";
    public override string DefaultCss => """
        .cupri-spinner { display:inline-block; width:24px; height:24px; color:var(--cupri-accent,#B87333);
                         animation: cupri-spin 0.9s linear infinite; }
        @keyframes cupri-spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "progressbar");
        el.SetAttribute("data-cupri-icon", Icons.Get("autorenew") ?? "");
        el.ClassList.Add("cupri-spinner");
    }
}

/// <summary><c>&lt;cupri-skeleton&gt;</c> — a pulsing placeholder block for loading states.</summary>
public sealed class SkeletonComponent : ComponentBase
{
    public override string Tag => "cupri-skeleton";
    public override string DefaultCss => """
        .cupri-skeleton { display:block; height:16px; background:#e2e6ec; border-radius:6px;
                          animation: cupri-pulse 1.4s ease-in-out infinite; }
        @keyframes cupri-pulse { 0% { opacity:1; } 50% { opacity:0.4; } 100% { opacity:1; } }
        """;

    public override void Expand(IElement el) => el.ClassList.Add("cupri-skeleton");
}
