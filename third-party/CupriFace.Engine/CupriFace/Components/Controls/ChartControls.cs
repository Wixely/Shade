using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>A single chart datum: a value with an optional label and per-item colour.</summary>
internal readonly record struct ChartPoint(double Value, string Label, string? Color);

/// <summary>
/// Shared data reading for the chart components. Supports BOTH APIs: child elements
/// (<c>&lt;cupri-bar value label color&gt;</c>) when present, else <c>values="1,2,3"</c> (+ optional
/// <c>labels="a,b,c"</c>) attributes — both bindable to a model string.
/// </summary>
internal static class ChartData
{
    // A small palette for multi-colour cases; single-series charts use one accent by default.
    internal const string Accent = "#B87333";
    internal const string Line = "#4682B4";
    private static readonly string[] _palette = { "#4682B4", "#e0245e", "#10ac84", "#f5b301", "#9b59b6", "#B87333", "#576574" };
    internal static string Palette(int i) => _palette[System.Math.Abs(i) % _palette.Length];

    internal static List<ChartPoint> Read(IElement el, string childTag)
    {
        var pts = new List<ChartPoint>();
        var children = el.Children.Where(c => c.LocalName == childTag).ToList();
        if (children.Count > 0)
        {
            foreach (var c in children)
                pts.Add(new ChartPoint(
                    Parse(c.GetAttribute("value")),
                    c.GetAttribute("label") ?? c.TextContent.Trim(),
                    c.GetAttribute("color")));
        }
        else
        {
            var values = Split(el.GetAttribute("values"));
            var labels = Split(el.GetAttribute("labels"));
            for (var i = 0; i < values.Length; i++)
                pts.Add(new ChartPoint(Parse(values[i]), i < labels.Length ? labels[i].Trim() : "", null));
        }
        return pts;
    }

    // The value that maps to the top of the plot: the `max` attribute, else the largest datum.
    internal static double Max(IElement el, IReadOnlyList<ChartPoint> s)
    {
        if (double.TryParse(el.GetAttribute("max"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0) return m;
        return s.Count == 0 ? 1 : System.Math.Max(1e-6, s.Max(p => p.Value));
    }

    // Normalised polyline points "x,y x,y …" in 0..1 (y=0 top), with a vertical inset so the line never
    // hugs the edges. fullWidth spans 0..1 (sparkline/rolling); otherwise the points sit at flex-slot
    // centres so they line up with the x-axis labels (line chart). By default the Y axis auto-scales to
    // the data's min..max; pass fixedMin/fixedMax for a stable range (a rolling monitor), which also
    // clamps out-of-range samples into the box.
    internal static string Points(IReadOnlyList<ChartPoint> s, bool fullWidth, double inset = 0.12,
        double? fixedMin = null, double? fixedMax = null)
    {
        if (s.Count == 0) return "";
        var min = fixedMin ?? s.Min(p => p.Value);
        var max = fixedMax ?? s.Max(p => p.Value);
        var range = max - min;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Count; i++)
        {
            var x = fullWidth ? (s.Count == 1 ? 0.5 : (double)i / (s.Count - 1)) : (i + 0.5) / s.Count;
            var norm = range > 1e-9 ? System.Math.Clamp((s[i].Value - min) / range, 0, 1) : 0.5;
            var y = inset + (1 - norm) * (1 - 2 * inset);
            if (i > 0) sb.Append(' ');
            sb.Append(Fmt(x)).Append(',').Append(Fmt(y));
        }
        return sb.ToString();
    }

    internal static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    internal static double Parse(string? v) => double.TryParse((v ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    internal static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    // A hover tooltip anchored above a chart element (the element must carry the matching id).
    internal static string Tip(string id, string text) =>
        $"<div class='cupri-chart-tip' data-cupri-anchor='{id}' data-cupri-placement='top'>{Esc(text)}</div>";

    // An invisible hover target for a line-chart point at normalised (x,y) in the plot, carrying its tooltip.
    internal static string Dot(string id, double x, double y, string color, string tip) =>
        $"<div class='cupri-lc-dot' id='{id}' style='left:{Fmt(x * 100)}%;top:{Fmt(y * 100)}%;color:{color}'>{Tip(id, tip)}</div>";
    internal static List<double> Vals(string? csv) => Split(csv).Select(Parse).ToList();
    private static string[] Split(string? v) => (v ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries);

    // A "nice" axis top + tick step for 0..dataMax: round the step to 1/2/5×10^n so gridlines land on
    // tidy numbers, and round the top up to a whole number of steps.
    internal static (double Max, double Step) NiceAxis(double dataMax, int ticks = 4)
    {
        if (dataMax <= 0) return (1, 1);
        var raw = dataMax / ticks;
        var mag = System.Math.Pow(10, System.Math.Floor(System.Math.Log10(raw)));
        var norm = raw / mag;
        var nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        var step = nice * mag;
        return (System.Math.Ceiling(dataMax / step) * step, step);
    }

    // The y-axis labels (top→bottom) + horizontal gridlines for a 0..axisMax scale. The plot then scales
    // its bars/line to axisMax so they line up with the gridlines.
    internal static (string YAxis, string Grid, double AxisMax) Axis(double dataMax)
    {
        var (axisMax, step) = NiceAxis(dataMax);
        var n = (int)System.Math.Round(axisMax / step);
        var ya = new StringBuilder("<div class='cupri-yaxis'>");
        var grid = new StringBuilder();
        for (var i = n; i >= 0; i--) // top (max) down to 0
        {
            ya.Append($"<span>{Fmt(i * step)}</span>");
            grid.Append($"<div class='cupri-gridline' style='top:{Fmt((1 - (double)i / n) * 100)}%'></div>");
        }
        ya.Append("</div>");
        return (ya.ToString(), grid.ToString(), axisMax);
    }
}

/// <summary>
/// <c>&lt;cupri-bar-chart values="12,19,7" labels="Mon,Tue,Wed"&gt;</c> — a vertical bar chart. A single
/// series comes from <c>values="…"</c> or <c>&lt;cupri-bar value label color&gt;</c> children; MULTIPLE
/// series from <c>&lt;cupri-series values="…" color label&gt;</c> children, shown side-by-side (grouped)
/// or summed (<c>stacked</c>). Add <c>axis</c> for a y-axis + gridlines on a tidy 0..max scale.
/// </summary>
public sealed class BarChartComponent : ComponentBase
{
    public override string Tag => "cupri-bar-chart";
    public override string DefaultCss => """
        .cupri-bar-chart { display:block; }
        .cupri-bc-plot { display:flex; align-items:flex-end; gap:8px; height:150px; }
        .cupri-bc-plot.baseline { border-bottom:2px solid var(--cupri-border, #cbd2dc); }
        .cupri-bc-bar { flex:1; min-height:2px; border-radius:5px 5px 0 0; }
        .cupri-bc-group { flex:1; display:flex; align-items:flex-end; gap:3px; height:100%; }
        .cupri-bc-stack { flex:1; display:flex; flex-direction:column-reverse; height:100%; }
        .cupri-bc-seg { width:100%; min-height:1px; }
        .cupri-bc-labels { display:flex; gap:8px; margin-top:7px; }
        .cupri-bc-labels > span { flex:1; text-align:center; font-size:12px; color:var(--cupri-muted, #98a2b3); }
        /* y-axis + gridlines, shared with the line chart. */
        .cupri-chart-row { display:flex; }
        .cupri-yaxis { display:flex; flex-direction:column; justify-content:space-between; width:34px; height:150px;
                       padding-right:7px; text-align:right; }
        .cupri-yaxis > span { font-size:11px; color:var(--cupri-muted, #98a2b3); line-height:1; }
        .cupri-plot { position:relative; flex:1; height:150px; }
        .cupri-gridline { position:absolute; left:0; width:100%; height:1px; background:var(--cupri-border, #e6e9f0); }
        /* Hover tooltip shared by every element-based chart (bars, stacked segments, heatmap cells): a
           fixed bubble anchored above the hovered element, revealed by [data-hover] on that element. */
        .cupri-chart-tip { position:fixed; display:none; background:#1e2430; color:white; padding:5px 9px; border-radius:6px;
                           font-size:12px; z-index:40; white-space:nowrap; box-shadow:0 4px 14px #00000033; }
        [data-hover] > .cupri-chart-tip { display:block; }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-bar-chart");
        var axis = Flag(el, "axis");
        var stacked = Flag(el, "stacked");

        var seriesEls = el.Children.Where(c => c.LocalName == "cupri-series").ToList();
        double dataMax;
        string bars;
        List<string> labels;
        List<(string Label, string Color)>? legend = null;

        if (seriesEls.Count > 0) // grouped / stacked: one <cupri-series> per data series
        {
            var series = seriesEls.Select((c, i) => (
                Vals: ChartData.Vals(c.GetAttribute("values")),
                Color: c.GetAttribute("color") is { Length: > 0 } col ? col : ChartData.Palette(i),
                Label: c.GetAttribute("label") ?? "")).ToList();
            var cats = series.Max(s => s.Vals.Count);
            dataMax = stacked
                ? Enumerable.Range(0, cats).Select(ci => series.Sum(s => ci < s.Vals.Count ? s.Vals[ci] : 0)).DefaultIfEmpty(0).Max()
                : series.SelectMany(s => s.Vals).DefaultIfEmpty(0).Max();
            var max = Scale(el, axis, dataMax);
            var sb = new StringBuilder();
            for (var ci = 0; ci < cats; ci++)
            {
                sb.Append(stacked ? "<div class='cupri-bc-stack'>" : "<div class='cupri-bc-group'>");
                foreach (var s in series)
                {
                    var v = ci < s.Vals.Count ? s.Vals[ci] : 0;
                    var pct = max > 0 ? System.Math.Clamp(v / max * 100.0, 0, 100) : 0;
                    var id = NextId();
                    var tip = s.Label.Length > 0 ? $"{s.Label}: {ChartData.Fmt(v)}" : ChartData.Fmt(v);
                    sb.Append($"<div class='{(stacked ? "cupri-bc-seg" : "cupri-bc-bar")}' id='{id}' style='height:{ChartData.Fmt(pct)}%;background:{s.Color}'>{ChartData.Tip(id, tip)}</div>");
                }
                sb.Append("</div>");
            }
            bars = sb.ToString();
            labels = LabelList(el, cats);
            legend = series.Select(s => (s.Label, s.Color)).ToList();
        }
        else // single series
        {
            var pts = ChartData.Read(el, "cupri-bar");
            dataMax = pts.Count > 0 ? pts.Max(p => p.Value) : 1;
            var max = Scale(el, axis, dataMax);
            labels = LabelList(el, pts.Count);
            if (labels.All(l => l.Length == 0)) labels = pts.Select(p => p.Label).ToList();
            var sb = new StringBuilder();
            for (var i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var pct = max > 0 ? System.Math.Clamp(p.Value / max * 100.0, 0, 100) : 0;
                var id = NextId();
                var lab = i < labels.Count ? labels[i] : "";
                var tip = lab.Length > 0 ? $"{lab}: {ChartData.Fmt(p.Value)}" : ChartData.Fmt(p.Value);
                sb.Append($"<div class='cupri-bc-bar' id='{id}' style='height:{ChartData.Fmt(pct)}%;background:{p.Color ?? ChartData.Accent}'>{ChartData.Tip(id, tip)}</div>");
            }
            bars = sb.ToString();
        }

        var plot = $"<div class='cupri-bc-plot{(axis ? "" : " baseline")}'>{bars}</div>";
        var body = new StringBuilder();
        if (axis)
        {
            var (ya, grid, _) = ChartData.Axis(dataMax);
            body.Append($"<div class='cupri-chart-row'>{ya}<div class='cupri-plot'>{grid}{plot}</div></div>");
            AppendLabels(body, labels, 34);
        }
        else
        {
            body.Append(plot);
            AppendLabels(body, labels, 0);
        }
        if (legend is { } lg && lg.Any(s => s.Label.Length > 0))
        {
            body.Append("<div class='cupri-lc-legend'>");
            foreach (var (lab, col) in lg)
                body.Append($"<span class='cupri-lc-key'><span class='cupri-lc-swatch' style='background:{col}'></span>{ChartData.Esc(lab)}</span>");
            body.Append("</div>");
        }
        el.InnerHtml = body.ToString();
    }

    private static void AppendLabels(StringBuilder body, List<string> labels, int padLeft)
    {
        if (!labels.Any(l => l.Length > 0)) return;
        body.Append(padLeft > 0 ? $"<div class='cupri-bc-labels' style='padding-left:{padLeft}px'>" : "<div class='cupri-bc-labels'>");
        foreach (var l in labels) body.Append($"<span>{ChartData.Esc(l)}</span>");
        body.Append("</div>");
    }

    // The bar-scaling denominator: a tidy axis max when `axis` is on, else the `max` attr, else the data.
    private static double Scale(IElement el, bool axis, double dataMax) => axis
        ? ChartData.NiceAxis(dataMax).Max
        : (double.TryParse(el.GetAttribute("max"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : System.Math.Max(1e-6, dataMax));

    private static List<string> LabelList(IElement el, int count)
    {
        var raw = (el.GetAttribute("labels") ?? "").Split(',');
        var list = new List<string>();
        for (var i = 0; i < count; i++) list.Add(i < raw.Length ? raw[i].Trim() : "");
        return list;
    }
}

/// <summary>A bar inside <c>&lt;cupri-bar-chart&gt;</c> (value / label / color); consumed by the parent.</summary>
public sealed class BarComponent : ComponentBase
{
    public override string Tag => "cupri-bar";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}

/// <summary>A data series inside <c>&lt;cupri-bar-chart&gt;</c> (values / color / label) for grouped or
/// stacked bars; consumed by the parent.</summary>
public sealed class SeriesComponent : ComponentBase
{
    public override string Tag => "cupri-series";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}

/// <summary>
/// <c>&lt;cupri-line-chart values="…" labels="…" area dots curve&gt;</c> — a trend line auto-scaled to its
/// data. <c>area</c> fills under it, <c>dots</c> marks points, <c>curve</c> smooths it. For MULTIPLE
/// lines, give it <c>&lt;cupri-line values="…" color="…" label="…"&gt;</c> children (they share one Y axis
/// and get a legend); a single series can use <c>values="…"</c> or <c>&lt;cupri-point&gt;</c> children.
/// </summary>
public sealed class LineChartComponent : ComponentBase
{
    public override string Tag => "cupri-line-chart";
    public override string DefaultCss => """
        .cupri-line-chart { display:block; }
        .cupri-lc-plot { position:relative; display:block; height:150px; }
        .cupri-lc-line { position:absolute; top:0; left:0; width:100%; height:100%; color:#4682B4; }
        .cupri-lc-labels { display:flex; margin-top:7px; }
        .cupri-lc-labels > span { flex:1; text-align:center; font-size:12px; color:var(--cupri-muted, #98a2b3); }
        .cupri-lc-legend { display:flex; gap:16px; margin-top:9px; }
        .cupri-lc-key { display:inline-flex; align-items:center; gap:6px; font-size:12px; color:var(--cupri-muted, #98a2b3); }
        .cupri-lc-swatch { width:11px; height:11px; border-radius:3px; }
        /* Per-point hover target (invisible) → shows a marker + the value tooltip on hover. */
        .cupri-lc-dot { position:absolute; width:16px; height:16px; margin:-8px 0 0 -8px; border-radius:50%; }
        .cupri-lc-dot[data-hover] { background:currentColor; box-shadow:0 0 0 3px var(--cupri-surface,#fff); }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-line-chart");
        var axis = Flag(el, "axis");

        // Multiple series (<cupri-line …> children) or a single series (values / <cupri-point> children).
        var lineEls = el.Children.Where(c => c.LocalName == "cupri-line").ToList();
        var series = lineEls.Count > 0
            ? lineEls.Select((c, i) => (Pts: ChartData.Read(c, "cupri-point"),
                Color: c.GetAttribute("color") is { Length: > 0 } col ? col : ChartData.Palette(i),
                Label: c.GetAttribute("label") ?? "")).ToList()
            : new() { (ChartData.Read(el, "cupri-point"), Str(el, "color", ChartData.Line), "") };

        // With `axis`, scale to a tidy 0..max so the line lines up with the gridlines; otherwise auto-scale
        // to the data's own range (a tighter trend). One shared range across all series either way.
        var all = series.SelectMany(s => s.Pts).ToList();
        var dataMax = all.Count > 0 ? all.Max(p => p.Value) : 1;
        double min = axis ? 0 : (all.Count > 0 ? all.Min(p => p.Value) : 0);
        var max = axis ? ChartData.NiceAxis(dataMax).Max : dataMax;

        var flags = (Flag(el, "area") ? " data-cupri-area" : "")
                  + (Flag(el, "dots") ? " data-cupri-dots" : "")
                  + (Flag(el, "curve") ? " data-cupri-curve" : "");

        // x-axis labels (also used for the per-point tooltips): the `labels` attr, else the point labels.
        var labels = Str(el, "labels") is { Length: > 0 } lab
            ? lab.Split(',').Select(l => l.Trim()).ToList()
            : series[0].Pts.Select(p => p.Label).ToList();

        const double inset = 0.12; // matches ChartData.Points, so the hover dots sit on the drawn line
        var range = max - min;
        var lines = new StringBuilder(); // each series overlays as an absolute line + invisible hover dots
        foreach (var s in series)
        {
            lines.Append($"<div class='cupri-lc-line' data-cupri-line='{ChartData.Points(s.Pts, fullWidth: false, fixedMin: min, fixedMax: max)}'{flags} style='color:{s.Color}'></div>");
            for (var i = 0; i < s.Pts.Count; i++)
            {
                var x = (i + 0.5) / s.Pts.Count;
                var norm = range > 1e-9 ? System.Math.Clamp((s.Pts[i].Value - min) / range, 0, 1) : 0.5;
                var y = inset + (1 - norm) * (1 - 2 * inset);
                var xlab = i < labels.Count ? labels[i] : "";
                var tip = (s.Label.Length > 0 ? s.Label + " · " : "") + (xlab.Length > 0 ? xlab + ": " : "") + ChartData.Fmt(s.Pts[i].Value);
                lines.Append(ChartData.Dot(NextId(), x, y, s.Color, tip));
            }
        }
        var plot = $"<div class='cupri-lc-plot'>{lines}</div>";

        var body = new StringBuilder();
        if (axis)
        {
            var (ya, grid, _) = ChartData.Axis(dataMax);
            body.Append($"<div class='cupri-chart-row'>{ya}<div class='cupri-plot'>{grid}{plot}</div></div>");
        }
        else body.Append(plot);

        // x-axis labels (computed above for the tooltips).
        if (labels.Any(l => l.Length > 0))
        {
            body.Append(axis ? "<div class='cupri-lc-labels' style='padding-left:34px'>" : "<div class='cupri-lc-labels'>");
            foreach (var l in labels) body.Append($"<span>{ChartData.Esc(l)}</span>");
            body.Append("</div>");
        }

        // Legend for multiple named series.
        if (series.Count > 1 && series.Any(s => s.Label.Length > 0))
        {
            body.Append("<div class='cupri-lc-legend'>");
            foreach (var s in series)
                body.Append($"<span class='cupri-lc-key'><span class='cupri-lc-swatch' style='background:{s.Color}'></span>{ChartData.Esc(s.Label)}</span>");
            body.Append("</div>");
        }
        el.InnerHtml = body.ToString();
    }
}

/// <summary>A point inside a line chart / sparkline (value / label); consumed by the parent.</summary>
public sealed class PointComponent : ComponentBase
{
    public override string Tag => "cupri-point";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}

/// <summary>A series inside <c>&lt;cupri-line-chart&gt;</c> (values / color / label) for multi-line
/// charts; consumed by the parent.</summary>
public sealed class LineSeriesComponent : ComponentBase
{
    public override string Tag => "cupri-line";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}

/// <summary>
/// <c>&lt;cupri-sparkline values="…" area&gt;</c> — a compact, axis-less trend line that sizes to its box
/// (defaults inline), for stat cards or inline text. Spans the full width; <c>area</c>/<c>dots</c> optional.
/// </summary>
public sealed class SparklineComponent : ComponentBase
{
    public override string Tag => "cupri-sparkline";
    public override string DefaultCss => """
        .cupri-sparkline { display:inline-block; width:120px; height:34px; color:#4682B4; vertical-align:middle; }
        """;

    public override void Expand(IElement el)
    {
        var series = ChartData.Read(el, "cupri-point");
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-sparkline");
        el.SetAttribute("data-cupri-line", ChartData.Points(series, fullWidth: true));
        if (Flag(el, "area")) el.SetAttribute("data-cupri-area", "");
        if (Flag(el, "dots")) el.SetAttribute("data-cupri-dots", "");
        if (Flag(el, "curve")) el.SetAttribute("data-cupri-curve", "");
        el.InnerHtml = ""; // the polyline is painted directly on this element's box
    }
}

/// <summary>
/// <c>&lt;cupri-rolling-chart values="{{Hist}}" max="200"&gt;</c> — a time-series monitor (Task-Manager
/// style): a full-width area line over a FIXED 0..<c>max</c> range, so new samples scroll in from the
/// right without the baseline jumping. Bind <c>values</c> to a rolling window your model maintains and
/// let the refresh cadence drive it (no dots/labels; the plot is tinted, bordered and clips its line).
/// </summary>
public sealed class RollingChartComponent : ComponentBase
{
    public override string Tag => "cupri-rolling-chart";
    public override string DefaultCss => """
        .cupri-rolling { display:block; }
        .cupri-rl-plot { display:block; height:130px; overflow:hidden; color:#4682B4;
                         background:var(--cupri-hover, #eef1f5); border:1px solid var(--cupri-border, #cbd2dc);
                         border-radius:8px; }
        """;

    public override void Expand(IElement el)
    {
        var series = ChartData.Read(el, "cupri-point");
        var max = ChartData.Max(el, series); // fixed ceiling: the `max` attr, else the window's largest
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-rolling");

        var pts = ChartData.Points(series, fullWidth: true, inset: 0.06, fixedMin: 0, fixedMax: max);
        var curve = Flag(el, "curve") ? " data-cupri-curve" : "";
        var color = Str(el, "color");
        var style = color.Length > 0 ? $" style='color:{color}'" : "";
        el.InnerHtml = $"<div class='cupri-rl-plot' data-cupri-line='{pts}' data-cupri-area{curve}{style}></div>";
    }
}

/// <summary>
/// <c>&lt;cupri-heatmap values="…" columns="7"&gt;</c> (or <c>&lt;cupri-heat value&gt;</c> children) — a grid
/// of cells tinted by intensity (contribution-graph style); darker = higher, relative to <c>max</c>.
/// </summary>
public sealed class HeatmapComponent : ComponentBase
{
    public override string Tag => "cupri-heatmap";
    public override string DefaultCss => """
        .cupri-heatmap { display:grid; gap:4px; }
        .cupri-hm-cell { height:22px; border-radius:4px; }
        """;

    public override void Expand(IElement el)
    {
        var series = ChartData.Read(el, "cupri-heat");
        var max = ChartData.Max(el, series);
        var cols = System.Math.Max(1, (int)Num(el, "columns", 7));
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-heatmap");
        el.SetAttribute("style", $"grid-template-columns:repeat({cols},1fr)");

        var sb = new StringBuilder();
        foreach (var p in series)
        {
            var t = System.Math.Clamp(p.Value / max, 0, 1);
            var alpha = ChartData.Fmt(0.12 + 0.88 * t); // faint → solid accent
            var id = NextId();
            var tip = p.Label.Length > 0 ? $"{p.Label}: {ChartData.Fmt(p.Value)}" : ChartData.Fmt(p.Value);
            sb.Append($"<div class='cupri-hm-cell' id='{id}' style='background:rgba(184,115,51,{alpha})'>{ChartData.Tip(id, tip)}</div>");
        }
        el.InnerHtml = sb.ToString();
    }
}

/// <summary>A cell inside <c>&lt;cupri-heatmap&gt;</c> (value); consumed by the parent.</summary>
public sealed class HeatCellComponent : ComponentBase
{
    public override string Tag => "cupri-heat";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}
