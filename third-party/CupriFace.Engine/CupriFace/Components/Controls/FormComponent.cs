using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-form name="signup"&gt;</c> — a boundary around a group of fields.
///
/// <para>The engine had no form concept at all: <c>ValidateAll</c> was document-wide, and a submit
/// scope existed only as a convention — whatever <c>data-…</c> attribute an app happened to hang on
/// an ancestor for <c>OnSubmit</c> to bubble to. That works, but it is a rule you have to know, and
/// two independent forms on one page could not be validated apart: submitting the login box reported
/// AND revealed the half-finished signup box's errors.</para>
///
/// <para>This makes the boundary a thing you can point at, and it needs no new dispatch to do it:</para>
/// <list type="bullet">
/// <item>it emits <c>data-cupri-form="name"</c>, which <see cref="CupriDocument.Validate(string)"/>
/// scopes to — so one form's Submit shows one form's errors;</item>
/// <item>that same attribute is what <c>OnSubmit</c> bubbles to, so
/// <c>doc.OnSubmit("data-cupri-form", e =&gt; …)</c> hands you the form's NAME in <c>e.Value</c> and
/// Enter in any single-line field inside it submits, exactly as Enter in an <c>&lt;input&gt;</c>
/// submits its form on the web.</item>
/// </list>
///
/// <para>So the two halves an app previously wired by hand — which fields to validate, and what a
/// submit belongs to — become the same declaration.</para>
/// </summary>
public sealed class FormComponent : ComponentBase
{
    public override string Tag => "cupri-form";

    public override string DefaultCss => """
        .cupri-form { display:flex; flex-direction:column; gap:12px; }
        .cupri-form-title { font-weight:bold; font-size:15px; color:var(--cupri-text, #1e2430); }
        """;

    public override void Expand(IElement el)
    {
        // Unnamed forms are allowed and are still a submit scope — they just cannot be validated by
        // name. Falling back to the empty string would make every unnamed form on a page the SAME
        // scope, which is worse than having none, so the attribute is simply absent.
        if (Str(el, "name") is { Length: > 0 } name)
            el.SetAttribute("data-cupri-form", name);

        el.SetAttribute("role", "form");
        if (Str(el, "label") is { Length: > 0 } label) el.SetAttribute("aria-label", label);
        el.ClassList.Add("cupri-form");

        if (Str(el, "title") is { Length: > 0 } title)
            el.InnerHtml = $"<div class='cupri-form-title'>{title}</div>" + el.InnerHtml;
    }
}
