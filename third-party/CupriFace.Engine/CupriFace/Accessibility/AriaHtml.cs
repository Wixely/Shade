using System.Globalization;
using System.Text;

namespace CupriFace.Accessibility;

/// <summary>
/// Serialises the platform-neutral semantics tree (<see cref="AccessibilityNode"/>) to an ARIA HTML
/// fragment. The web host injects this into an off-screen, screen-reader-visible DOM element that
/// mirrors the canvas — so assistive tech can read the UI the canvas paints opaquely (DESIGN §5).
/// Each node becomes a <c>&lt;div role="…"&gt;</c> carrying <c>aria-label</c> and the relevant states
/// (<c>aria-checked</c>, <c>aria-valuenow/min/max</c>, <c>aria-disabled</c>, <c>tabindex</c>).
/// </summary>
public static class AriaHtml
{
    public static string Serialize(AccessibilityNode root)
    {
        var sb = new StringBuilder();
        Write(sb, root);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, AccessibilityNode n)
    {
        sb.Append("<div role=\"").Append(Esc(n.Role)).Append('"');
        if (!string.IsNullOrEmpty(n.Name)) sb.Append(" aria-label=\"").Append(Esc(n.Name!)).Append('"');
        if (n.Focusable) sb.Append(" tabindex=\"0\"");
        if (n.Disabled) sb.Append(" aria-disabled=\"true\"");
        if (n.Checked is { } c) sb.Append(" aria-checked=\"").Append(c ? "true" : "false").Append('"');
        if (n.Selected is { } selected) sb.Append(" aria-selected=\"").Append(selected ? "true" : "false").Append('"');
        if (n.Expanded is { } expanded) sb.Append(" aria-expanded=\"").Append(expanded ? "true" : "false").Append('"');
        if (n.Now is { } now) sb.Append(" aria-valuenow=\"").Append(Num(now)).Append('"');
        if (n.Min is { } min) sb.Append(" aria-valuemin=\"").Append(Num(min)).Append('"');
        if (n.Max is { } max) sb.Append(" aria-valuemax=\"").Append(Num(max)).Append('"');
        sb.Append('>');

        // A leaf role reads its accessible name as text; containers recurse into their children.
        if (n.Children.Count == 0)
        {
            if (!string.IsNullOrEmpty(n.Name)) sb.Append(Esc(n.Name!));
        }
        else
        {
            foreach (var child in n.Children) Write(sb, child);
        }
        sb.Append("</div>");
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
