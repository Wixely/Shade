using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-taginput value="{{Tags}}" placeholder="Add a tag…"&gt;</c> — multi-value entry.
/// <c>cupri-combobox</c> picks one thing from a list; this collects several, which is what a labels
/// field, a recipient list or a filter set needs.
///
/// <para>The value is a comma-separated string, because that is what round-trips through the same
/// binding a text field already uses. Typing and pressing Enter appends (the engine's
/// <c>data-tag-list</c> hook); clicking a chip's × removes it.</para>
///
/// <para>Removal needs no new engine primitive, which is worth explaining because it looks like it
/// should: each chip carries the list it would leave behind, precomputed at expand time as an
/// ordinary <c>data-set-path</c>/<c>data-set-value</c> pair. The markup is rebuilt on every change
/// anyway, so "the list without me" is always current, and removal is the same click primitive that
/// drives a tab strip.</para>
/// </summary>
public sealed class TagInputComponent : ComponentBase
{
    public override string Tag => "cupri-taginput";

    public override string DefaultCss => """
        .cupri-taginput { display:flex; align-items:center; flex-wrap:wrap; gap:6px; min-width:220px;
                          background:var(--cupri-surface, white); border:2px var(--cupri-border, #cbd2dc);
                          border-radius:8px; padding:6px 8px; font-size:15px; }
        .cupri-taginput[data-hover] { border-color:#98a2b3; }
        .cupri-taginput[data-invalid] { border-color:#d92d20; }
        .cupri-tag { display:flex; align-items:center; gap:5px; background:var(--cupri-hover, #eef1f5);
                     color:var(--cupri-text, #1e2430); border-radius:6px; padding:3px 6px 3px 9px; font-size:13px; }
        .cupri-tag-x { color:var(--cupri-muted, #667085); font-size:14px; line-height:1; padding:0 2px; }
        .cupri-tag-x[data-hover] { color:#d92d20; }
        /* The entry sits on the same line as the chips and takes what is left, so the control reads as
           one field rather than a box with a box in it. */
        .cupri-tag-entry { flex:1; min-width:80px; border:0; padding:3px 2px; background:transparent;
                           color:var(--cupri-text, #1e2430); }
        .cupri-tag-ph { color:var(--cupri-muted, #98a2b3); }
        """;

    public override void Expand(IElement el)
    {
        var path = Str(el, "data-bind-value");
        var raw = Str(el, "value");
        var tags = raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        var sb = new StringBuilder();
        for (var i = 0; i < tags.Count; i++)
        {
            // The list this chip would leave behind — computed now, so removal is a plain set.
            var without = string.Join(",", tags.Where((_, j) => j != i));
            sb.Append($"<span class='cupri-tag'>{Esc(tags[i])}")
              .Append(path.Length > 0
                  ? $"<span class='cupri-tag-x' role='button' aria-label='Remove {Esc(tags[i])}' " +
                    $"data-set-path='{Esc(path)}' data-set-value='{Esc(without)}'>×</span>"
                  : "")
              .Append("</span>");
        }

        // The entry is its own bound field so the engine edits it as text; data-tag-list is what makes
        // Enter mean "append to the list" rather than "submit the surrounding form".
        var entryKey = Str(el, "entry-key", "__tagentry_" + (path.Length > 0 ? path : NextId()));
        var ph = Str(el, "placeholder");
        sb.Append($"<div class='cupri-tag-entry' role='textbox' data-bind-value='{Esc(entryKey)}'")
          .Append(path.Length > 0 ? $" data-tag-list='{Esc(path)}'" : "")
          .Append($" inputmode='text' enterkeyhint='done' placeholder='{Esc(ph)}'>")
          .Append(ph.Length > 0 ? $"<span class='cupri-tag-ph'>{Esc(ph)}</span>" : "​")
          .Append("</div>");

        el.SetAttribute("role", "group");
        el.SetAttribute("aria-label", Str(el, "label", "Tags"));
        el.ClassList.Add("cupri-taginput");
        el.InnerHtml = sb.ToString();
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&#39;");
}
