using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-menu label open&gt;…&lt;cupri-menu-item&gt;…&lt;/cupri-menu&gt;</c> — a dropdown menu.
/// Renders a trigger in flow and, when open, an anchored popup (top layer) below it.
/// </summary>
public sealed class MenuComponent : ComponentBase
{
    public override string Tag => "cupri-menu";
    public override string DefaultCss => """
        .cupri-menu { display:inline-block; }
        .cupri-menu-trigger { display:inline-flex; align-items:center; gap:6px; padding:9px 14px;
                              background:var(--cupri-hover,#eef1f5); color:var(--cupri-text,#1e2430);
                              border-radius:8px; font-weight:bold; font-size:14px; }
        .cupri-menu-popup { position:fixed; background:var(--cupri-surface,white); border-radius:10px; padding:6px; z-index:30;
                            border:1px var(--cupri-border,#e6e9f0); box-shadow:0 10px 28px #00000026; }
        """;

    public override void Expand(IElement el)
    {
        var open = Flag(el, "open");
        var label = Str(el, "label", "Menu");
        var id = NextId();
        var items = el.InnerHtml;

        el.ClassList.Add("cupri-menu");
        var trigger = $"<div class='cupri-menu-trigger' id='{id}' data-cupri-toggle=\"{id}\">{label}" +
                      IconMarkup("chevron-down", 16) + "</div>";
        var popup = open
            ? $"<div class='cupri-menu-popup' role='menu' data-focus-scope data-cupri-anchor='{id}' data-cupri-placement='bottom'>{items}</div>"
            : "";
        el.InnerHtml = trigger + popup;
    }
}

/// <summary>
/// <c>&lt;cupri-context-menu&gt;…&lt;cupri-menu-item&gt;…&lt;/cupri-context-menu&gt;</c> — attaches a
/// right-click menu to a region. The element's non-item children <b>are</b> the region; its
/// <c>&lt;cupri-menu-item&gt;</c>s (fly-out submenus and all) are moved into a popup that opens at the
/// pointer on right-click and dismisses on an outside click, Escape, or scroll. Wire an item's action
/// the usual way (a class + <c>OnClick</c>, or <c>data-set-path</c>); picking it also closes the menu.
/// </summary>
public sealed class ContextMenuComponent : ComponentBase
{
    public override string Tag => "cupri-context-menu";
    public override string DefaultCss => """
        .cupri-ctx-host { display:block; }
        .cupri-ctx-menu { position:fixed; display:none; background:var(--cupri-surface,white); border-radius:10px; padding:6px;
                          min-width:180px; z-index:60; border:1px var(--cupri-border,#e6e9f0); box-shadow:0 10px 28px #00000026; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-ctx-host");
        el.SetAttribute("data-cupri-ctx-host", ""); // the engine finds this region on right-click

        // The direct <cupri-menu-item>s are the menu; everything else stays put as the target region.
        var items = el.Children
            .Where(c => string.Equals(c.LocalName, "cupri-menu-item", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (items.Count == 0) return;

        var menu = el.Owner!.CreateElement("div");
        menu.ClassName = "cupri-ctx-menu";
        menu.SetAttribute("role", "menu");
        menu.SetAttribute("data-cupri-ctx-menu", "");
        menu.SetAttribute("data-focus-scope", "");
        foreach (var it in items) menu.AppendChild(it); // relocate (keeps identity, still gets expanded)
        el.AppendChild(menu);
    }
}

/// <summary>
/// <c>&lt;cupri-menu-item&gt;</c> — a row inside a menu. A plain row keeps its label; a row that
/// <b>contains its own <c>&lt;cupri-menu-item&gt;</c>s</b> becomes a fly-out submenu: it shows a
/// chevron and, on hover, reveals its children in a panel to the right (give the row a
/// <c>label</c> for its own text). The panel is <c>position:absolute</c> inside the menu popup, so
/// it paints and hit-tests within the popup with no extra plumbing; <c>left:100%</c> keeps it flush
/// (no gap to fall through and dismiss the menu). Nesting works to any depth.
/// </summary>
public sealed class MenuItemComponent : ComponentBase
{
    public override string Tag => "cupri-menu-item";
    public override string DefaultCss => """
        .cupri-menu-item { display:flex; align-items:center; gap:8px; padding:9px 12px; border-radius:6px;
                           color:var(--cupri-text,#1e2430); font-size:14px; }
        .cupri-menu-item:hover { background:var(--cupri-hover,#eef1f5); }
        .cupri-menu-label { flex:1; }                         /* push the chevron to the far edge */
        .cupri-menu-parent { position:relative; }
        .cupri-submenu { position:absolute; left:100%; top:-7px; display:none; min-width:170px;
                         background:var(--cupri-surface,white); border-radius:10px; padding:6px; z-index:31;
                         border:1px var(--cupri-border,#e6e9f0); box-shadow:0 10px 28px #00000026; }
        .cupri-menu-parent:hover > .cupri-submenu { display:block; } /* fly out while the row (or its panel) is hovered */
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "menuitem");
        el.ClassList.Add("cupri-menu-item");
        var icon = Str(el, "icon");

        // A row that holds its own menu items opens a fly-out submenu.
        var subItems = el.Children
            .Where(c => string.Equals(c.LocalName, "cupri-menu-item", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (subItems.Count > 0)
        {
            el.ClassList.Add("cupri-menu-parent");
            el.SetAttribute("aria-haspopup", "menu");

            // Move (not copy) the nested items into the fly-out panel so they keep their identity and
            // still get expanded by this same pass; AppendChild relocates an existing child.
            var flyout = el.Owner!.CreateElement("div");
            flyout.ClassName = "cupri-submenu";
            flyout.SetAttribute("role", "menu");
            foreach (var s in subItems) flyout.AppendChild(s);

            // The row's own label: the `label` attribute, else whatever text is left after the move.
            var label = Str(el, "label");
            if (label.Length == 0) label = el.TextContent.Trim();
            el.InnerHtml = (icon.Length > 0 ? IconMarkup(icon, 18) : "") +
                           $"<span class='cupri-menu-label'>{label}</span>" + IconMarkup("chevron-right", 16);
            el.AppendChild(flyout);
            return;
        }

        if (icon.Length > 0) el.InnerHtml = IconMarkup(icon, 18) + el.InnerHtml;
    }
}

/// <summary>
/// <c>&lt;cupri-tooltip text open&gt;…&lt;/cupri-tooltip&gt;</c> — wraps a trigger and shows an anchored
/// bubble above it. Shows on <b>hover</b> by default (via <c>:hover</c>); <c>open="true"</c> pins it
/// open regardless. The bubble is always in the DOM but hidden until revealed, so no re-expand is
/// needed — a hover just re-resolves styles (like any other <c>:hover</c> rule).
/// </summary>
public sealed class TooltipComponent : ComponentBase
{
    public override string Tag => "cupri-tooltip";
    public override string DefaultCss => """
        .cupri-tooltip { display:inline-block; }
        .cupri-tt-anchor { display:inline-block; }
        /* max-width, because the anchor clamp can only move a bubble, not shrink one: a long
           tooltip measured 712px and ran off a 393px phone from x=4. Wrapping is the fix. */
        .cupri-tt-bubble { position:fixed; background:#1e2430; color:white; padding:6px 10px;
                           border-radius:6px; font-size:12px; z-index:40; display:none; max-width:260px; width:max-content;
                           box-shadow:0 4px 14px #00000033; }
        .cupri-tooltip:hover .cupri-tt-bubble { display:inline-block; } /* reveal on hover */
        .cupri-tt-bubble.cupri-tt-open { display:inline-block; }        /* open="true" pins it */
        """;

    public override void Expand(IElement el)
    {
        var open = Flag(el, "open");
        var text = Str(el, "text");
        var id = NextId();
        var trigger = el.InnerHtml;

        el.ClassList.Add("cupri-tooltip");
        var bubble = text.Length > 0
            ? $"<div class='cupri-tt-bubble{(open ? " cupri-tt-open" : "")}' role='tooltip' " +
              $"data-cupri-anchor='{id}' data-cupri-placement='top'>{text}</div>"
            : "";
        el.InnerHtml = $"<div class='cupri-tt-anchor' id='{id}'>{trigger}</div>" + bubble;
    }
}
