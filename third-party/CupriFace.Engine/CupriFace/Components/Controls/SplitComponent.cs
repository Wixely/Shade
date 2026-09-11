using System;
using System.Linq;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-split&gt;&lt;cupri-split-panel&gt;…&lt;/cupri-split-panel&gt;…&lt;/cupri-split&gt;</c> —
/// resizable panels laid side by side (or stacked, with the <c>vertical</c> flag), separated by draggable
/// dividers this inserts between each pair. Dragging a divider grows one panel and shrinks its neighbour
/// (adjusting their flex‑grow); the split ratio survives rebuilds. Give the split a bounded size.
/// </summary>
public sealed class SplitComponent : ComponentBase
{
    public override string Tag => "cupri-split";
    public override string DefaultCss => """
        .cupri-split { display:flex; }
        .cupri-split.vertical { flex-direction:column; }
        .cupri-split-panel { flex:1 1 0; overflow:auto; min-width:0; min-height:0; }
        .cupri-split-divider { flex:none; width:7px; align-self:stretch; background:var(--cupri-border, #e6e9f0); cursor:col-resize; }
        .cupri-split.vertical > .cupri-split-divider { width:auto; height:7px; cursor:row-resize; }
        .cupri-split-divider[data-hover] { background:var(--cupri-accent,#B87333); }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-split");
        if (Flag(el, "vertical")) el.ClassList.Add("vertical");

        // Insert a divider between each consecutive pair of panels (before components expand the panels).
        var panels = el.Children.Where(c => string.Equals(c.LocalName, "cupri-split-panel", StringComparison.OrdinalIgnoreCase)).ToList();
        for (var i = 1; i < panels.Count; i++)
        {
            var divider = el.Owner!.CreateElement("div");
            divider.ClassName = "cupri-split-divider";
            el.InsertBefore(divider, panels[i]);
        }
    }
}

/// <summary>One panel of a <c>&lt;cupri-split&gt;</c>. <c>size</c> sets its initial share (flex‑grow, default 1).</summary>
public sealed class SplitPanelComponent : ComponentBase
{
    public override string Tag => "cupri-split-panel";
    public override string DefaultCss => ""; // styled by SplitComponent's .cupri-split-panel rule
    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-split-panel");
        var style = el.GetAttribute("style");
        el.SetAttribute("style", (string.IsNullOrEmpty(style) ? "" : style + ";") + $"flex:{F(Num(el, "size", 1))} 1 0");
    }
}
