using System.Linq;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-breadcrumb&gt;&lt;cupri-crumb href="reports"&gt;Reports&lt;/cupri-crumb&gt;…&lt;/cupri-breadcrumb&gt;</c>
/// — the trail back up a hierarchy. The LAST crumb is where you are: it renders as plain text with
/// <c>aria-current="page"</c> and is deliberately not a link, because a link to the page you are on
/// is a dead control that still looks live.
///
/// <para>A crumb routes like anything else: <c>href</c> raises <c>Navigated</c> (the app routes
/// internal hrefs), or <c>value</c> with a bound path on the container writes that value straight to
/// the model. Give it neither and it is a label — useful for a level that has no page of its own.</para>
/// </summary>
public sealed class BreadcrumbComponent : ComponentBase
{
    public override string Tag => "cupri-breadcrumb";

    public override string DefaultCss => """
        .cupri-breadcrumb { display:flex; align-items:center; flex-wrap:wrap; gap:6px; font-size:14px; }
        .cupri-crumb { color:var(--cupri-muted, #667085); }
        .cupri-crumb[data-hover] { color:var(--cupri-text, #1e2430); }
        .cupri-crumb-link { color:var(--cupri-accent, #B87333); font-weight:600; }
        .cupri-crumb-link[data-hover] { opacity:0.75; }
        /* The separator is decorative: aria-hidden keeps a screen reader from reading "slash"
           between every level, which is noise in a list that is already a nav landmark. */
        .cupri-crumb-sep { color:var(--cupri-border, #cbd2dc); }
        .cupri-crumb-current { color:var(--cupri-text, #1e2430); font-weight:bold; }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var sep = Str(el, "separator", "/");
        var crumbs = el.Children.Where(c => c.LocalName == "cupri-crumb").ToList();

        var sb = new StringBuilder();
        for (var i = 0; i < crumbs.Count; i++)
        {
            var c = crumbs[i];
            var last = i == crumbs.Count - 1;
            var label = c.InnerHtml;
            var href = Str(c, "href");
            var value = Str(c, "value");

            if (i > 0)
                sb.Append($"<span class='cupri-crumb-sep' aria-hidden='true'>{Esc(sep)}</span>");

            if (last)
                sb.Append($"<span class='cupri-crumb cupri-crumb-current' aria-current='page'>{label}</span>");
            else if (href.Length > 0)
                sb.Append($"<a class='cupri-crumb cupri-crumb-link' href='{Esc(href)}'>{label}</a>");
            else if (value.Length > 0 && path.Length > 0)
                sb.Append($"<span class='cupri-crumb cupri-crumb-link' role='link' " +
                          $"data-set-path='{Esc(path)}' data-set-value='{Esc(value)}'>{label}</span>");
            else
                sb.Append($"<span class='cupri-crumb'>{label}</span>");
        }

        el.SetAttribute("role", "navigation");
        el.SetAttribute("aria-label", Str(el, "label", "Breadcrumb"));
        el.ClassList.Add("cupri-breadcrumb");
        el.InnerHtml = sb.ToString();
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&#39;");
}

/// <summary>Declares one level of a <see cref="BreadcrumbComponent"/>. Consumed by its parent — the
/// element itself renders nothing, like <c>cupri-tab</c> and <c>cupri-option</c>.</summary>
public sealed class CrumbComponent : ComponentBase
{
    public override string Tag => "cupri-crumb";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}

/// <summary>
/// <c>&lt;cupri-toolbar&gt;</c> — a row of controls that belong together, with
/// <c>role="toolbar"</c> so assistive technology announces it as one group rather than as loose
/// buttons. Put <c>&lt;cupri-toolbar-group&gt;</c>s inside to separate clusters; a group with
/// <c>push</c> takes the free space before it, which is how a right-aligned cluster is spelled
/// without an app writing a spacer div.
/// </summary>
public sealed class ToolbarComponent : ComponentBase
{
    public override string Tag => "cupri-toolbar";

    public override string DefaultCss => """
        .cupri-toolbar { display:flex; align-items:center; gap:8px; padding:8px 10px;
                         background:var(--cupri-surface, #fff); border:1px var(--cupri-border, #e6e9f0);
                         border-radius:10px; }
        .cupri-toolbar-group { display:flex; align-items:center; gap:6px; }
        /* margin-left:auto on a flex item eats the free space to its left — the whole point of the
           `push` flag, and the reason this is a class rather than something an app hand-rolls. */
        .cupri-toolbar-group.push { margin-left:auto; }
        .cupri-toolbar-sep { width:1px; align-self:stretch; margin:2px 2px;
                             background:var(--cupri-border, #e6e9f0); }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "toolbar");
        if (Str(el, "label") is { Length: > 0 } label) el.SetAttribute("aria-label", label);
        el.ClassList.Add("cupri-toolbar");
    }
}

/// <summary>A cluster inside a <see cref="ToolbarComponent"/>. <c>push</c> pins it (and everything
/// after it) to the far end.</summary>
public sealed class ToolbarGroupComponent : ComponentBase
{
    public override string Tag => "cupri-toolbar-group";
    public override string DefaultCss => "";

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "group");
        el.ClassList.Add("cupri-toolbar-group");
        if (Flag(el, "push")) el.ClassList.Add("push");
    }
}

/// <summary>A hairline between toolbar clusters. Decorative, so it is hidden from the a11y tree.</summary>
public sealed class ToolbarSeparatorComponent : ComponentBase
{
    public override string Tag => "cupri-toolbar-sep";
    public override string DefaultCss => "";

    public override void Expand(IElement el)
    {
        el.SetAttribute("aria-hidden", "true");
        el.ClassList.Add("cupri-toolbar-sep");
    }
}
