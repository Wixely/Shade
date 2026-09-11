using System;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-datepicker value="{{Date}}" open="{{Open}}"&gt;</c> — a date field bound to an ISO
/// <c>yyyy-MM-dd</c> string. The trigger shows the formatted date; clicking it opens an anchored
/// month calendar (top layer). Clicking a day writes that date and closes; the ‹ › buttons page
/// months in place (they set the value to the same day in the adjacent month via
/// <c>data-set-keep</c>, so the popup stays open). The shown month follows the selected value.
/// </summary>
public sealed class DatePickerComponent : ComponentBase
{
    public override string Tag => "cupri-datepicker";
    public override string DefaultCss => """
        .cupri-dp { display:inline-block; }
        .cupri-dp-trigger { display:inline-flex; align-items:center; justify-content:space-between; gap:10px;
                            min-width:180px; padding:9px 12px; background:var(--cupri-surface, white);
                            border:2px var(--cupri-border, #cbd2dc); border-radius:8px;
                            color:var(--cupri-text, #1e2430); font-size:15px; }
        .cupri-dp-trigger[data-hover] { border-color:#98a2b3; }
        .cupri-dp-ph { color:var(--cupri-muted, #98a2b3); }
        .cupri-dp-popup { position:fixed; background:var(--cupri-surface, white); border-radius:10px; padding:12px;
                          z-index:30; border:1px var(--cupri-border, #e6e9f0); width:250px; box-shadow:0 10px 28px #00000026; }
        .cupri-dp-head { display:flex; align-items:center; justify-content:space-between; margin-bottom:8px; }
        .cupri-dp-title { font-weight:bold; font-size:14px; color:var(--cupri-text, #1e2430); }
        .cupri-dp-nav { padding:4px 9px; border-radius:6px; color:var(--cupri-text, #1e2430); font-size:15px; }
        .cupri-dp-nav[data-hover] { background:var(--cupri-hover, #eef1f5); }
        .cupri-dp-grid { display:grid; grid-template-columns: repeat(7, 1fr); gap:2px; }
        .cupri-dp-dow { text-align:center; font-size:11px; color:#98a2b3; padding:4px 0; }
        /* Fixed cell height so every week-row is equal even when a row is all padding — keeps the
           popup a constant height across months (no edge-flip jitter while paging). */
        .cupri-dp-day, .cupri-dp-pad { text-align:center; font-size:13px; height:16px; padding:7px 0; }
        .cupri-dp-day { border-radius:6px; color:var(--cupri-text, #1e2430); }
        .cupri-dp-day[data-hover], .cupri-dp-day[data-highlight] { background:var(--cupri-hover, #eef1f5); }
        .cupri-dp-day.selected { background:var(--cupri-accent,#B87333); color:white; font-weight:bold; }
        """;

    private static readonly string[] Dow = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var value = Str(el, "value");
        var open = Flag(el, "open");
        var id = NextId();

        var selected = DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
        var view = selected ?? DateOnly.FromDateTime(DateTime.Today);
        var first = new DateOnly(view.Year, view.Month, 1);
        var startDow = ((int)first.DayOfWeek + 6) % 7; // Monday = 0
        var daysInMonth = DateTime.DaysInMonth(view.Year, view.Month);
        var keepDay = selected?.Day ?? 1;

        string Shift(int months)
        {
            var m = first.AddMonths(months);
            var dim = DateTime.DaysInMonth(m.Year, m.Month);
            return new DateOnly(m.Year, m.Month, Math.Min(keepDay, dim)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var body = new StringBuilder();
        if (open)
        {
            var title = $"{CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[view.Month - 1]} {view.Year}";
            body.Append($"<div class='cupri-dp-popup' role='dialog' data-focus-scope data-cupri-anchor='{id}' data-cupri-placement='bottom'>");
            body.Append("<div class='cupri-dp-head'>")
                .Append(path.Length > 0 ? $"<div class='cupri-dp-nav' role='button' data-set-path='{path}' data-set-value='{Shift(-1)}' data-set-keep>&#8249;</div>" : "<div class='cupri-dp-nav'>&#8249;</div>")
                .Append($"<div class='cupri-dp-title'>{title}</div>")
                .Append(path.Length > 0 ? $"<div class='cupri-dp-nav' role='button' data-set-path='{path}' data-set-value='{Shift(1)}' data-set-keep>&#8250;</div>" : "<div class='cupri-dp-nav'>&#8250;</div>")
                .Append("</div>");

            body.Append("<div class='cupri-dp-grid' data-gridnav='7'>"); // 7 cols → arrow-key day nav
            foreach (var w in Dow) body.Append($"<div class='cupri-dp-dow'>{w}</div>");
            for (var i = 0; i < startDow; i++) body.Append("<div class='cupri-dp-pad'></div>"); // leading blanks
            for (var day = 1; day <= daysInMonth; day++)
            {
                var iso = new DateOnly(view.Year, view.Month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var isSel = selected is { } s && s.Year == view.Year && s.Month == view.Month && s.Day == day;
                body.Append($"<div class='cupri-dp-day{(isSel ? " selected" : "")}' role='button'")
                    .Append(path.Length > 0 ? $" data-set-path='{path}' data-set-value='{iso}'" : "")
                    .Append($">{day}</div>");
            }
            // Always fill six week-rows (42 day cells). A month spans 4–6 rows; padding to a fixed
            // count keeps the popup height constant so viewport-edge flipping doesn't jump around as
            // you page months (the anchor logic measures the same height every month).
            for (var i = startDow + daysInMonth; i < 42; i++) body.Append("<div class='cupri-dp-pad'></div>");
            body.Append("</div></div>");
        }

        var label = selected is { } sel
            ? $"<span>{sel.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}</span>"
            : $"<span class='cupri-dp-ph'>{Escape(Str(el, "placeholder", "Pick a date"))}</span>";

        el.SetAttribute("role", "combobox");
        el.SetAttribute("aria-expanded", open ? "true" : "false");
        el.ClassList.Add("cupri-dp");
        el.InnerHtml =
            $"<div class='cupri-dp-trigger' id='{id}' data-cupri-toggle=\"{id}\">{label}{IconMarkup("calendar", 16)}</div>" +
            body;
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
