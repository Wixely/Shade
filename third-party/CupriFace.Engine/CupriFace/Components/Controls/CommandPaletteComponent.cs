using System.Linq;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-command-palette open="{{Open}}" value="{{Query}}"&gt;…&lt;cupri-command …&gt;Label&lt;/cupri-command&gt;…</c>
/// — a modal fuzzy-search over commands. Bind <c>open</c> (a toolbar button toggles it); when it opens
/// the search field auto-focuses, typing filters the commands (case-insensitive substring), ↑/↓ move a
/// highlight and Enter runs the highlighted one, clicking runs it, and Escape or the backdrop dismisses.
/// Each <c>&lt;cupri-command&gt;</c> carries a <c>data-set-path</c>/<c>data-set-value</c> (navigate, or set a
/// model field the app reacts to) plus an optional <c>icon</c>. Reuses the combobox typeahead machinery.
/// </summary>
public sealed class CommandPaletteComponent : ComponentBase
{
    public override string Tag => "cupri-command-palette";
    public override string DefaultCss => """
        .cupri-command-palette { display:block; }
        /* No left/right inset ⇒ the engine centres the fixed panel horizontally (transform would only
           move the paint, not the hit-test). */
        .cupri-cmdp-panel { position:fixed; top:72px; width:520px; background:var(--cupri-surface,#fff);
                            border-radius:14px; padding:10px; z-index:70; border:1px var(--cupri-border,#e6e9f0);
                            box-shadow:0 24px 60px #00000047; }
        .cupri-cmdp-input { display:block; background:var(--cupri-surface,#fff); border:2px var(--cupri-border,#cbd2dc);
                            border-radius:9px; padding:11px 14px; font-size:16px; color:var(--cupri-text,#1e2430); }
        .cupri-cmdp-input:focus { border-color:var(--cupri-accent,#B87333); }
        .cupri-cmdp-list { margin-top:8px; display:flex; flex-direction:column; gap:2px; max-height:320px; overflow:scroll; }
        /* A 520px panel is wider than a phone: it hung 74px off BOTH edges of a 393dp screen, so the
           query box and the command labels were cut off. Below the breakpoint it takes a share of
           the viewport instead (still centred — no left/right inset), and sits nearer the top since
           the soft keyboard will claim the bottom half. */
        @media (max-width: 600px) {
          .cupri-cmdp-panel { width:92%; top:16px; }
          .cupri-cmdp-list { max-height:240px; }
        }
        .cupri-cmdp-row { display:flex; align-items:center; gap:11px; padding:10px 12px; border-radius:8px;
                          color:var(--cupri-text,#1e2430); font-size:14px; }
        .cupri-cmdp-row[data-hover], .cupri-cmdp-row[data-highlight] { background:var(--cupri-hover,#eef1f5); }
        .cupri-cmdp-empty { padding:12px; color:#98a2b3; font-size:14px; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-command-palette");
        var path = el.GetAttribute("data-bind-value") ?? "";  // the query binding (value="{{…}}")
        el.RemoveAttribute("data-bind-value");                 // the inner field owns it; data-bind-open stays on the host
        if (!Flag(el, "open")) { el.InnerHtml = ""; el.SetAttribute("style", "display:none"); return; }

        var query = Str(el, "value").Trim();

        var commands = el.Children
            .Where(c => string.Equals(c.LocalName, "cupri-command", System.StringComparison.OrdinalIgnoreCase))
            .Select(c => (Label: c.TextContent.Trim(), Icon: Str(c, "icon"),
                          Path: c.GetAttribute("data-set-path") ?? "", Value: c.GetAttribute("data-set-value") ?? ""))
            .ToList();
        var matches = query.Length == 0 ? commands
            : commands.Where(c => c.Label.Contains(query, System.StringComparison.OrdinalIgnoreCase)).ToList();

        var list = new StringBuilder("<div class='cupri-cmdp-list' role='listbox'>");
        if (matches.Count == 0)
            list.Append("<div class='cupri-cmdp-empty'>No matching commands</div>");
        else
            foreach (var (label, icon, sp, sv) in matches)
                list.Append("<div class='cupri-cmdp-row' role='option'")
                    .Append(sp.Length > 0 ? $" data-set-path='{sp}' data-set-value='{Attr(sv)}'" : "")
                    .Append('>')
                    .Append(icon.Length > 0 ? IconMarkup(icon, 18) : "")
                    .Append($"<span>{Escape(label)}</span></div>");
        list.Append("</div>");

        var display = query.Length > 0
            ? $"<span class='cupri-tf-text' data-caret-anchor>{Escape(query)}</span>"
            : "<span class='cupri-tf-ph' data-caret-anchor>Type a command…</span>";
        // data-listbox → ↑/↓/Enter nav; data-autofocus → the caret lands here when the palette opens.
        var input = $"<div class='cupri-cmdp-input' role='textbox' id='{NextId()}' data-listbox data-autofocus"
                  + (path.Length > 0 ? $" data-bind-value='{path}'" : "")
                  + $">{display}</div>";

        el.SetAttribute("role", "dialog");
        el.SetAttribute("aria-modal", "true");
        el.InnerHtml =
            "<div class='cupri-backdrop' data-cupri-dismiss='true'></div>" +
            $"<div class='cupri-cmdp-panel' data-focus-scope>{input}{list}</div>";
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Attr(string s) => s.Replace("'", "&#39;");
}
