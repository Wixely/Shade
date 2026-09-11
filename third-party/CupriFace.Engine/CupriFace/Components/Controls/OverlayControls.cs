using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-dialog open blur&gt;</c> — a modal dialog: a full-viewport backdrop plus a centred
/// panel, both lifted to the top layer (position:fixed). Clicking the backdrop dismisses it. Add
/// <c>blur="{{Flag}}"</c> to frost the page behind it (backdrop-filter).
/// </summary>
public sealed class DialogComponent : ComponentBase
{
    public override string Tag => "cupri-dialog";
    // .cupri-backdrop (and its .blurred variant) is the shared scrim used by the dialog, drawer and
    // shelf — defined once here, reused by class name across all three overlay components.
    public override string DefaultCss => """
        .cupri-dialog { display:block; }
        .cupri-backdrop { position:fixed; top:0; left:0; width:100%; height:100%; background:#00000099; }
        .cupri-backdrop.blurred { background:#00000055; backdrop-filter:blur(9px); }
        .cupri-dialog-panel { position:fixed; width:360px; background:white; border-radius:14px;
                              padding:24px; z-index:10; box-shadow:0 18px 50px #00000040; }
        """;

    public override void Expand(IElement el)
    {
        el.ClassList.Add("cupri-dialog");
        if (!Flag(el, "open")) { el.InnerHtml = ""; el.SetAttribute("style", "display:none"); return; }

        el.SetAttribute("role", "dialog");
        el.SetAttribute("aria-modal", "true");
        var content = el.InnerHtml;
        el.InnerHtml =
            $"<div class='{Backdrop(el)}' data-cupri-dismiss=\"true\"></div>" +
            $"<div class='cupri-dialog-panel' data-focus-scope>{content}</div>";
    }

    /// <summary>The scrim's class list — adds <c>blurred</c> (backdrop-filter) when <c>blur</c> is set.
    /// Shared by the dialog, drawer and shelf.</summary>
    internal static string Backdrop(IElement el) => Flag(el, "blur") ? "cupri-backdrop blurred" : "cupri-backdrop";
}

/// <summary>
/// <c>&lt;cupri-toast&gt;</c> — a transient message pinned to the bottom-right (top layer).
/// </summary>
public sealed class ToastComponent : ComponentBase
{
    public override string Tag => "cupri-toast";
    public override string DefaultCss => """
        .cupri-toast { position:fixed; bottom:24px; right:24px; max-width:320px; z-index:20;
                       background:#1e2430; color:white; padding:14px 18px; border-radius:10px; font-size:14px;
                       box-shadow:0 10px 28px #00000040; }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "status");
        el.ClassList.Add("cupri-toast");
    }
}

/// <summary>
/// Styling for the engine-owned toast <b>stack</b> raised via <c>doc.Toast("…")</c>: a bottom-right
/// column of messages that slide in, sit for a few seconds, then slide out and are removed. This
/// component carries no markup of its own — its CSS is always available (component CSS is aggregated
/// regardless of use), so the injected toasts have their look + enter/exit transition. Pass a kind
/// (<c>"success"</c>/<c>"error"</c>) to <c>doc.Toast</c> to tint one.
/// </summary>
public sealed class ToasterComponent : ComponentBase
{
    public override string Tag => "cupri-toaster";
    public override string DefaultCss => """
        .cupri-toaster { position:fixed; bottom:24px; right:24px; display:flex; flex-direction:column;
                         gap:10px; align-items:flex-end; z-index:80; }
        .cupri-toast-item { background:#1e2430; color:white; padding:13px 18px; border-radius:10px; font-size:14px;
                            max-width:340px; box-shadow:0 10px 28px #00000040;
                            transition: transform 0.34s ease, opacity 0.34s ease; }
        .cupri-toast-item.success { background:#12805c; }
        .cupri-toast-item.error { background:#b23c3c; }
        """;
    public override void Expand(IElement el) { } // CSS-only; toasts are injected by the engine (doc.Toast)
}
