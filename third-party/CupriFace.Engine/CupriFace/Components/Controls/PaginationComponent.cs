using System;
using System.Collections.Generic;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-pagination page="{{Page}}" pages="10"&gt;</c> — a 1-based page navigator bound to the
/// current page. Renders ‹ prev, the first/last pages, a small window around the current page (with
/// … gaps), and next ›. Clicking a page or arrow writes the new page number.
/// </summary>
public sealed class PaginationComponent : ComponentBase
{
    public override string Tag => "cupri-pagination";
    // Every slot (page numbers AND the … gaps) is the same fixed width, and the slot COUNT is
    // constant, so the control keeps exactly one width regardless of the current page — no shifting
    // as you page through (see BuildSlots).
    public override string DefaultCss => """
        /* wrap: the slot count is fixed by design (no shifting as you page), which on a phone
           makes the strip wider than the screen unless it is allowed onto a second line. */
        .cupri-pagination { display:inline-flex; gap:4px; align-items:center; flex-wrap:wrap; max-width:100%; }
        .cupri-page, .cupri-page-nav, .cupri-page-ell { min-width:32px; height:32px; padding:0 6px; border-radius:6px;
                                       font-size:14px; display:inline-flex; align-items:center; justify-content:center;
                                       color:var(--cupri-text, #1e2430); }
        .cupri-page[data-hover], .cupri-page-nav[data-hover] { background:var(--cupri-hover, #eef1f5); }
        .cupri-page.active { background:var(--cupri-accent,#B87333); color:white; font-weight:bold; }
        .cupri-page-ell { color:var(--cupri-muted, #98a2b3); }
        .cupri-page-nav.disabled { color:var(--cupri-border, #cbd2dc); }
        """;

    private const int Window = 7; // fixed count of number/ellipsis slots between the nav arrows

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-page") ?? "";
        var pages = Math.Max(1, (int)Num(el, "pages", 1));
        var page = Math.Clamp((int)Num(el, "page", 1), 1, pages);

        el.SetAttribute("role", "navigation");
        el.ClassList.Add("cupri-pagination");

        var sb = new StringBuilder();
        Nav(sb, "chevron-left", page > 1, path, page - 1);
        foreach (var slot in BuildSlots(page, pages))
        {
            if (slot == 0) { sb.Append("<div class='cupri-page-ell'>…</div>"); continue; }
            var active = slot == page;
            sb.Append($"<div class='cupri-page{(active ? " active" : "")}' role='button' aria-label='page {slot}'")
              .Append(path.Length > 0 && !active ? $" data-set-path='{path}' data-set-value='{slot}'" : "")
              .Append($">{slot}</div>");
        }
        Nav(sb, "chevron-right", page < pages, path, page + 1);
        el.InnerHtml = sb.ToString();
    }

    // The slots to render (0 = an … gap). Always exactly <see cref="Window"/> slots once there are
    // enough pages, with first/last pinned and a window that slides with the current page — so the
    // layout width never changes as you navigate. Small page counts (≤ Window) show every page (and
    // never shift either, since all pages are always present).
    private static IEnumerable<int> BuildSlots(int page, int pages)
    {
        if (pages <= Window)
        {
            for (var p = 1; p <= pages; p++) yield return p;
            yield break;
        }
        if (page <= 4) // near the start: 1 2 3 4 5 … last
        {
            for (var p = 1; p <= 5; p++) yield return p;
            yield return 0; yield return pages;
        }
        else if (page >= pages - 3) // near the end: 1 … last-4 … last
        {
            yield return 1; yield return 0;
            for (var p = pages - 4; p <= pages; p++) yield return p;
        }
        else // middle: 1 … p-1 p p+1 … last
        {
            yield return 1; yield return 0;
            yield return page - 1; yield return page; yield return page + 1;
            yield return 0; yield return pages;
        }
    }

    private static void Nav(StringBuilder sb, string icon, bool enabled, string path, int target)
    {
        sb.Append($"<div class='cupri-page-nav{(enabled ? "" : " disabled")}' role='button'")
          .Append(enabled && path.Length > 0 ? $" data-set-path='{path}' data-set-value='{target}'" : "")
          .Append($">{IconMarkup(icon, 16)}</div>");
    }
}
