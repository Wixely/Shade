using AngleSharp.Dom;
using CupriFace.Components;
using CupriFace.Components.Controls;

namespace Shade;

// Keep CupriFace's masking/editing implementation, but name the actual editable child.
// The released component leaves aria-label on its non-focusable outer wrapper.
internal sealed class AccessiblePasswordComponent : ICupriComponent
{
    private readonly PasswordFieldComponent component = new();
    public string Tag => component.Tag;
    public string DefaultCss => component.DefaultCss;
    public void Expand(IElement element)
    {
        component.Expand(element);
        if (element.QuerySelector("[role='textbox']") is { } field)
        {
            field.SetAttribute("aria-label", element.GetAttribute("aria-label") ?? "Password");
            field.SetAttribute("autocomplete", "off");
        }
    }
}
