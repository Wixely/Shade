using AngleSharp.Dom;

namespace CupriFace.Components;

/// <summary>
/// A custom element (DESIGN.md §10). Given a source element (with its bound
/// attributes), it expands into a subtree of primitive elements, attaches default
/// classes, and bakes in accessibility semantics (role/aria-*). First-party controls
/// and third-party components implement this same contract.
/// </summary>
public interface ICupriComponent
{
    /// <summary>Custom element tag this component handles, e.g. <c>cupri-slider</c>.</summary>
    string Tag { get; }

    /// <summary>Scoped default CSS for the component's internal parts.</summary>
    string DefaultCss { get; }

    /// <summary>Expand the element in place: set inner markup, classes, and aria-* roles.</summary>
    void Expand(IElement element);
}

/// <summary>Shared attribute-reading helpers for components.</summary>
public abstract class ComponentBase : ICupriComponent
{
    public abstract string Tag { get; }
    public abstract string DefaultCss { get; }
    public abstract void Expand(IElement element);

    protected static string Str(IElement el, string name, string fallback = "") =>
        el.GetAttribute(name) is { Length: > 0 } v ? v : fallback;

    protected static double Num(IElement el, string name, double fallback) =>
        double.TryParse(el.GetAttribute(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    protected static bool Flag(IElement el, string name)
    {
        var v = el.GetAttribute(name);
        if (v is null) return false;
        v = v.Trim().ToLowerInvariant();
        return v is not "false" and not "0" and not "no"; // empty/present ⇒ true
    }

    protected static double Percent(double value, double min, double max) =>
        max > min ? Math.Clamp((value - min) / (max - min), 0, 1) * 100.0 : 0.0;

    protected static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static int _idCounter;
    /// <summary>A unique element id for wiring an anchor to its popup.</summary>
    protected static string NextId() => "cupri-anchor-" + System.Threading.Interlocked.Increment(ref _idCounter);

    /// <summary>Markup for a leaf icon element (SVG path filled with current color). The per-use
    /// <paramref name="size"/> is the *default* — it's the fallback of the <c>--cupri-icon-size</c>
    /// variable, so authors can restyle icon size from CSS (e.g. <c>.cupri-icon { --cupri-icon-size: 20px }</c>
    /// or set the token on an ancestor) without fighting an inline width. Every icon also gets the
    /// <c>cupri-icon</c> class hook.</summary>
    protected static string IconMarkup(string name, int size, string cssClass = "")
    {
        var d = Icons.Get(name);
        if (d is null) return "";
        var cls = string.IsNullOrEmpty(cssClass) ? "cupri-icon" : $"cupri-icon {cssClass}";
        return $"<div class='{cls}' data-cupri-icon=\"{d}\" " +
               $"style='width:var(--cupri-icon-size, {size}px);height:var(--cupri-icon-size, {size}px)'></div>";
    }
}
