using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-button variant&gt;</c> — a themed button that keeps its child label. role=button.
/// variant="primary" (default) or "ghost".
/// </summary>
public sealed class ButtonComponent : ComponentBase
{
    public override string Tag => "cupri-button";

    public override string DefaultCss => """
        .cupri-button { display:inline-block; padding:10px 18px; border-radius:8px;
                        background:var(--cupri-accent,#B87333); color:white; font-weight:bold; font-size:15px; }
        .cupri-button.ghost { background:transparent; color:var(--cupri-accent,#B87333); border:2px var(--cupri-accent,#B87333); }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "button");
        el.ClassList.Add("cupri-button");
        if (Str(el, "variant") == "ghost") el.ClassList.Add("ghost");
        // Child label content is preserved as-is.
    }
}
