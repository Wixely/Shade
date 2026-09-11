using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-icon name size&gt;</c> — a vector icon from the built-in set, filled with the
/// current text color. Decorative by default (aria-hidden); set <c>aria-label</c> for an
/// image role.
/// </summary>
public sealed class IconComponent : ComponentBase
{
    public override string Tag => "cupri-icon";
    public override string DefaultCss => ".cupri-icon { display:inline-block; }";

    public override void Expand(IElement el)
    {
        var size = (int)Num(el, "size", 24);
        var path = Icons.Get(Str(el, "name")) ?? "";

        el.SetAttribute("data-cupri-icon", path);
        el.ClassList.Add("cupri-icon");

        var style = el.GetAttribute("style");
        el.SetAttribute("style", (string.IsNullOrEmpty(style) ? "" : style + ";") + $"width:{size}px;height:{size}px");

        if (el.HasAttribute("aria-label")) el.SetAttribute("role", "img");
        else el.SetAttribute("aria-hidden", "true");
    }
}
