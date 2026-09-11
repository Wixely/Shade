using System;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-color value="{{Hex}}" open="{{Open}}"&gt;</c> — a colour field bound to a <c>#RRGGBB</c>
/// string. The trigger shows a chip of the current colour plus its hex; clicking it opens an anchored
/// palette (top layer) of hue×shade swatches and a neutral ramp. Clicking a swatch writes that hex and
/// closes; the swatch matching the current value is ringed. Like the other popups it needs
/// <c>open="{{…}}"</c> bound to a bool so the open state survives the per-keystroke rebuild.
/// </summary>
public sealed class ColorComponent : ComponentBase
{
    public override string Tag => "cupri-color";
    public override string DefaultCss => """
        .cupri-color { display:inline-block; }
        .cupri-color-trigger { display:inline-flex; align-items:center; gap:10px; min-width:150px; padding:8px 11px;
                               background:var(--cupri-surface, white); border:2px var(--cupri-border, #cbd2dc);
                               border-radius:8px; color:var(--cupri-text, #1e2430); font-size:15px; }
        .cupri-color-trigger[data-hover] { border-color:#98a2b3; }
        .cupri-color-chip { width:20px; height:20px; border-radius:5px; border:1px solid #00000022; }
        .cupri-color-hex { flex:1; font-family:monospace; font-size:13px; }
        .cupri-color-ph { flex:1; color:var(--cupri-muted, #98a2b3); font-size:14px; }
        .cupri-color-pop { position:fixed; width:278px; background:var(--cupri-surface, white); border-radius:10px;
                           padding:10px; z-index:30; border:1px var(--cupri-border, #e6e9f0); box-shadow:0 10px 28px #00000026; }
        .cupri-color-grid { display:grid; grid-template-columns: repeat(10, 1fr); gap:4px; }
        .cupri-color-sw { height:22px; border-radius:5px; border:1px solid #00000018; }
        /* The neutral ramp is the last row of the SAME grid (see Expand) — the gap that sets it apart
           is a margin, not a second grid, so arrow-key navigation runs through it too. */
        .cupri-color-sw.neutral { margin-top:9px; }
        .cupri-color-sw[data-hover] { box-shadow:0 0 0 2px var(--cupri-accent, #B87333); }
        /* The keyboard cursor. Matches the hover ring so arrowing around reads the same as pointing. */
        .cupri-color-sw[data-highlight] { box-shadow:0 0 0 2px var(--cupri-accent, #B87333); }
        .cupri-color-sw.selected { box-shadow:0 0 0 2px var(--cupri-surface, #fff), 0 0 0 4px var(--cupri-accent, #B87333); }
        """;

    // 10 hues across the spectrum × 5 shades (light→dark); then a 10-step neutral ramp white→black.
    private static readonly double[] Hues = [0, 25, 45, 80, 140, 175, 205, 235, 275, 320];
    private static readonly double[] Shades = [0.80, 0.66, 0.53, 0.42, 0.32];
    private static readonly double[] Grays = [1.0, 0.86, 0.72, 0.58, 0.44, 0.32, 0.22, 0.14, 0.07, 0.0];

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-value") ?? "";
        var value = Str(el, "value");
        var cur = Normalize(value);
        var open = Flag(el, "open");
        var id = NextId();

        var body = new StringBuilder();
        if (open)
        {
            body.Append($"<div class='cupri-color-pop' role='dialog' data-focus-scope data-cupri-anchor='{id}' data-cupri-placement='bottom'>");
            // ONE grid: the hue×shade block then the neutral ramp as its final row. Arrow-key
            // navigation walks a single [data-gridnav] container, so splitting these into two grids
            // left the greys unreachable from the keyboard — the ramp is separated by a margin instead.
            body.Append("<div class='cupri-color-grid' data-gridnav='10'>");
            foreach (var l in Shades)
                foreach (var h in Hues)
                    body.Append(Swatch(Hsl(h, 0.72, l), cur, path));
            foreach (var l in Grays)
                body.Append(Swatch(Hsl(0, 0, l), cur, path, "neutral"));
            body.Append("</div></div>");
        }

        var label = value.Length > 0
            ? $"<span class='cupri-color-chip' style='background:{Esc(value)}'></span><span class='cupri-color-hex'>{Esc(value.ToUpperInvariant())}</span>"
            : $"<span class='cupri-color-chip' style='background:#00000010'></span><span class='cupri-color-ph'>{Esc(Str(el, "placeholder", "Pick a colour"))}</span>";

        el.SetAttribute("role", "combobox");
        el.SetAttribute("aria-expanded", open ? "true" : "false");
        el.ClassList.Add("cupri-color");
        el.InnerHtml =
            $"<div class='cupri-color-trigger' id='{id}' data-cupri-toggle=\"{id}\">{label}{IconMarkup("chevron-down", 16)}</div>" +
            body;
    }

    // One palette cell — sets the bound value (and closes, no data-set-keep) when clicked; ringed if current.
    private static string Swatch(string hex, string cur, string path, string extra = "")
    {
        var sel = string.Equals(hex, cur, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
        var cls = extra.Length > 0 ? " " + extra : "";
        var wire = path.Length > 0 ? $" role='button' data-set-path='{path}' data-set-value='{hex}'" : "";
        return $"<div class='cupri-color-sw{sel}{cls}' style='background:{hex}'{wire} title='{hex}'></div>";
    }

    // Accept #rgb / #rrggbb (any case) → canonical #RRGGBB for comparison; anything else stays as-is.
    private static string Normalize(string v)
    {
        v = v.Trim();
        if (v.StartsWith('#') && v.Length == 4) // #abc → #aabbcc
            v = $"#{v[1]}{v[1]}{v[2]}{v[2]}{v[3]}{v[3]}";
        return v.ToUpperInvariant();
    }

    // HSL (h∈[0,360), s,l∈[0,1]) → #RRGGBB.
    private static string Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60.0, x = c * (1 - Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;
        if (hp < 1) { r = c; g = x; }
        else if (hp < 2) { r = x; g = c; }
        else if (hp < 3) { g = c; b = x; }
        else if (hp < 4) { g = x; b = c; }
        else if (hp < 5) { r = x; b = c; }
        else { r = c; b = x; }
        double m = l - c / 2;
        return $"#{To(r + m)}{To(g + m)}{To(b + m)}";
    }

    private static string To(double v) => ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&#39;");
}
