using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-table&gt;&lt;cupri-row header&gt;&lt;cupri-cell&gt;…&lt;/cupri-cell&gt;…&lt;/cupri-row&gt;…&lt;/cupri-table&gt;</c>
/// — a simple data table. Rows are flex rows; cells share the width. For a scrolling body,
/// set <c>style="height:…;overflow:scroll"</c> on the table (the engine's overflow scroller
/// takes over). Pairs with collection binding to render rows from a model list.
///
/// Add <c>sort="{{Sort}}"</c> to make it <b>sortable</b>: header cells become click triggers that set
/// the bound sort key (<c>"col:dir"</c>, e.g. <c>"1:desc"</c>, toggling asc↔desc), and the body rows
/// are reordered by that column (numeric when both cells parse as numbers, else case‑insensitive text).
/// The active column shows a ▲/▼ marker.
///
/// Add <c>resize="{{Cols}}"</c> to make columns <b>resizable</b>: <c>Cols</c> is a comma list of per‑column
/// content widths in px (blank = auto). Drag a header cell's right boundary and that column's width is
/// written back to the list and applied to every row's matching cell (columns stay aligned); the last
/// column is left flexible so the table keeps filling its box.
/// </summary>
public sealed class TableComponent : ComponentBase
{
    public override string Tag => "cupri-table";
    public override string DefaultCss => """
        .cupri-table { display:block; border:1px var(--cupri-border, #e6e9f0); border-radius:10px;
                       color:var(--cupri-text, #1e2430); font-size:14px; }
        .cupri-cell[data-sortable] { color:var(--cupri-muted, #4a5262); }
        .cupri-cell[data-sortable][data-hover] { background:var(--cupri-hover, #eef1f5); }
        /* Resizable table: header cells show a column divider you can drag from the right edge. */
        .cupri-table[data-cupri-colresize] .cupri-row.header .cupri-cell { border-right:1px var(--cupri-border, #e6e9f0); }
        .cupri-table[data-cupri-colresize] .cupri-row.header .cupri-cell:last-child { border-right:0px transparent; }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "table");
        el.ClassList.Add("cupri-table");

        if (el.GetAttribute("data-bind-sort") is { Length: > 0 } sortPath) ApplySort(el, sortPath);
        if (el.GetAttribute("data-bind-select") is { Length: > 0 } selPath) ApplySelection(el, selPath);
        if (el.HasAttribute("resize")) ApplyResize(el);
    }

    // Resizable table: tag every cell with its column index and pin each fixed column (all but the last,
    // which stays flexible) to its width from the "resize" list, applied to header + body alike so columns
    // line up. The drag itself lives in CupriDocument (it writes the width back into the bound list).
    private static void ApplyResize(IElement el)
    {
        var rows = el.Children.Where(r => r.LocalName == "cupri-row").ToList();
        if (rows.Count == 0) return;
        var header = rows.FirstOrDefault(r => r.HasAttribute("header"));
        var colCount = (header ?? rows[0]).Children.Count(x => x.LocalName == "cupri-cell");
        if (colCount == 0) return;
        var widths = (el.GetAttribute("resize") ?? "").Split(',');

        foreach (var row in rows)
        {
            var cells = row.Children.Where(x => x.LocalName == "cupri-cell").ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                cells[c].SetAttribute("data-col", c.ToString(CultureInfo.InvariantCulture));
                if (c < colCount - 1 && c < widths.Length
                    && double.TryParse(widths[c], NumberStyles.Any, CultureInfo.InvariantCulture, out var w) && w > 0)
                {
                    var style = cells[c].GetAttribute("style") ?? "";
                    // Append so it beats the .cupri-cell{flex:1} rule and any earlier inline flex (last wins).
                    cells[c].SetAttribute("style", $"{style};flex:0 0 {w.ToString("0.##", CultureInfo.InvariantCulture)}px");
                }
            }
        }
        if (el.GetAttribute("data-bind-resize") is { Length: > 0 } path) el.SetAttribute("data-cupri-colresize", path);
    }

    // Sortable table: header cells become click triggers that set the bound "col:dir" key, and the body
    // rows are reordered by the chosen column (numeric when both cells parse, else case-insensitive text).
    private static void ApplySort(IElement el, string sortPath)
    {
        var sortCol = -1;
        var asc = true;
        var parts = (el.GetAttribute("sort") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], out var c)) { sortCol = c; asc = parts.Length < 2 || parts[1] != "desc"; }

        var rows = el.Children.Where(r => r.LocalName == "cupri-row").ToList();
        var header = rows.FirstOrDefault(r => r.HasAttribute("header"));
        var body = rows.Where(r => r != header).ToList();

        if (header is not null)
        {
            var headCells = header.Children.Where(x => x.LocalName == "cupri-cell").ToList();
            for (var i = 0; i < headCells.Count; i++)
            {
                var next = i == sortCol && asc ? $"{i}:desc" : $"{i}:asc";
                headCells[i].SetAttribute("data-set-path", sortPath);
                headCells[i].SetAttribute("data-set-value", next);
                headCells[i].SetAttribute("data-sortable", "");
                if (i == sortCol) headCells[i].InnerHtml += asc ? " &#9650;" : " &#9660;"; // ▲ / ▼
            }
        }
        if (sortCol >= 0 && body.Count > 1)
        {
            static string Cell(IElement row, int col)
            {
                var cells = row.Children.Where(x => x.LocalName == "cupri-cell").ToList();
                return col < cells.Count ? cells[col].TextContent.Trim() : "";
            }
            body = body.OrderBy(r => Cell(r, sortCol), new SortComparer(asc)).ToList();
            foreach (var r in body) el.AppendChild(r); // AppendChild relocates an existing child to the end
        }
    }

    // Selectable table: each body row toggles its (document-order) index in the bound comma-set on click;
    // rows already in the set are flagged data-selected for the highlight. Multi-select — click each row.
    private static void ApplySelection(IElement el, string selPath)
    {
        var sel = new HashSet<string>((el.GetAttribute("select") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        el.SetAttribute("data-cupri-select", "");
        var body = el.Children.Where(r => r.LocalName == "cupri-row" && !r.HasAttribute("header")).ToList();
        for (var i = 0; i < body.Count; i++)
        {
            body[i].SetAttribute("data-set-toggle", selPath);
            body[i].SetAttribute("data-toggle-value", i.ToString());
            if (sel.Contains(i.ToString())) body[i].SetAttribute("data-selected", "");
        }
    }

    // Numeric when both sides parse as numbers, else case-insensitive text; direction applied here.
    private sealed class SortComparer(bool asc) : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            var cmp = double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var na)
                   && double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var nb)
                ? na.CompareTo(nb)
                : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return asc ? cmp : -cmp;
        }
    }
}

/// <summary><c>&lt;cupri-row [header]&gt;</c> — a table row; <c>header</c> styles it as the head row.</summary>
public sealed class TableRowComponent : ComponentBase
{
    public override string Tag => "cupri-row";
    public override string DefaultCss => """
        .cupri-row { display:flex; border-bottom:1px var(--cupri-border, #eef1f5); }
        .cupri-row:last-child { border-bottom:0px transparent; }
        /* The header pins to the top of a scrolling table (needs an opaque fill to cover rows sliding under). */
        .cupri-row.header { background:var(--cupri-hover, #f6f7f9); font-weight:bold; color:var(--cupri-muted, #4a5262);
                            position:sticky; top:0; z-index:1; }
        .cupri-row[data-hover]:not(.header) { background:var(--cupri-hover, #f8f9fb); }
        .cupri-row[data-selected] { background:var(--cupri-hover, #eef1f5); box-shadow:inset 3px 0 0 var(--cupri-accent, #B87333); }
        """;

    public override void Expand(IElement el)
    {
        var header = Flag(el, "header");
        el.SetAttribute("role", "row");
        el.ClassList.Add("cupri-row");
        if (header) el.ClassList.Add("header");
    }
}

/// <summary><c>&lt;cupri-cell&gt;</c> — a table cell; shares row width evenly (flex:1).</summary>
public sealed class TableCellComponent : ComponentBase
{
    public override string Tag => "cupri-cell";
    public override string DefaultCss => """
        .cupri-cell { flex:1; padding:11px 14px; }
        """;

    public override void Expand(IElement el)
    {
        // A cell in a header row is a column header for a11y.
        var isHead = el.ParentElement?.ClassList.Contains("header") == true
                     || el.ParentElement?.HasAttribute("header") == true;
        el.SetAttribute("role", isHead ? "columnheader" : "cell");
        el.ClassList.Add("cupri-cell");
    }
}
