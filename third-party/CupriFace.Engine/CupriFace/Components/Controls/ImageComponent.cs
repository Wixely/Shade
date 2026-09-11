using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-image src="…" alt="…" fit="contain|cover|fill|none"&gt;</c> — a raster image (PNG/
/// JPEG/WebP/GIF). <c>src</c> resolves through the resource pipeline: a bare name like
/// <c>Assets/logo.png</c> is embedded in the app, or use a <c>data:</c> URI, <c>https://</c> URL, or
/// <c>file://</c> path. Size it with CSS <c>width</c>/<c>height</c> (aspect preserved if only one is
/// given); default fit is <c>contain</c>.
/// </summary>
public sealed class ImageComponent : ComponentBase
{
    public override string Tag => "cupri-image";
    public override string DefaultCss => ".cupri-image { display:block; overflow:hidden; }";

    public override void Expand(IElement el)
    {
        el.SetAttribute("data-cupri-image", Str(el, "src"));
        el.SetAttribute("data-object-fit", Str(el, "fit", "contain"));
        el.ClassList.Add("cupri-image");
        var alt = Str(el, "alt");
        if (alt.Length > 0) { el.SetAttribute("role", "img"); el.SetAttribute("aria-label", alt); }
        else el.SetAttribute("aria-hidden", "true"); // decorative
    }
}
