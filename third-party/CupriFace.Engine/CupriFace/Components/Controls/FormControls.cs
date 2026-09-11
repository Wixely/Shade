using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary><c>&lt;cupri-checkbox checked&gt;</c> — role=checkbox, ticks with an icon.</summary>
public sealed class CheckboxComponent : ComponentBase
{
    public override string Tag => "cupri-checkbox";
    public override string DefaultCss => """
        .cupri-checkbox { display:inline-flex; align-items:center; justify-content:center;
                          width:20px; height:20px; border:2px #98a2b3; border-radius:5px; }
        .cupri-checkbox.on { background:var(--cupri-accent,#B87333); border:2px var(--cupri-accent,#B87333); color:white; }
        """;

    public override void Expand(IElement el)
    {
        var on = Flag(el, "checked");
        el.SetAttribute("role", "checkbox");
        el.SetAttribute("aria-checked", on ? "true" : "false");
        el.ClassList.Add("cupri-checkbox");
        if (on) el.ClassList.Add("on");
        el.InnerHtml = on ? IconMarkup("check", 14) : "";
    }
}

/// <summary>
/// <c>&lt;cupri-radio&gt;</c> — role=radio. Either a standalone bound toggle
/// (<c>checked="{{X}}"</c>) or one option in a group (<c>group="{{Sel}}" value="a"</c>),
/// where clicking selects this option for the whole group.
/// </summary>
public sealed class RadioComponent : ComponentBase
{
    public override string Tag => "cupri-radio";
    public override string DefaultCss => """
        .cupri-radio { display:inline-flex; align-items:center; justify-content:center;
                       width:20px; height:20px; border:2px #98a2b3; border-radius:10px; }
        .cupri-radio.on { border:2px var(--cupri-accent,#B87333); }
        .cupri-radio-dot { width:10px; height:10px; background:var(--cupri-accent,#B87333); border-radius:5px; }
        """;

    public override void Expand(IElement el)
    {
        var value = Str(el, "value");
        var on = value.Length > 0 ? Str(el, "group") == value : Flag(el, "checked");

        el.SetAttribute("role", "radio");
        el.SetAttribute("aria-checked", on ? "true" : "false");
        el.ClassList.Add("cupri-radio");
        if (on) el.ClassList.Add("on");
        el.InnerHtml = on ? "<div class='cupri-radio-dot'></div>" : "";
    }
}

/// <summary><c>&lt;cupri-icon-button icon&gt;</c> — a compact button showing only an icon.</summary>
public sealed class IconButtonComponent : ComponentBase
{
    public override string Tag => "cupri-icon-button";
    public override string DefaultCss => """
        .cupri-icon-button { display:inline-flex; align-items:center; justify-content:center; width:38px; height:38px;
                             border-radius:8px; background:var(--cupri-hover,#eef1f5); color:var(--cupri-muted,#48505c); }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "button");
        el.ClassList.Add("cupri-icon-button");
        el.InnerHtml = IconMarkup(Str(el, "icon"), 20);
    }
}
