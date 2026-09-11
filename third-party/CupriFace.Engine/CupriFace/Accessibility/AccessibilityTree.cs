using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;

namespace CupriFace.Accessibility;

/// <summary>
/// A platform-neutral semantics node (DESIGN.md §5). The render tree paints to a flat
/// canvas, so this parallel tree is what assistive tech consumes — bridged to UIA /
/// AT-SPI / NSAccessibility on desktop and a hidden DOM overlay on web.
/// </summary>
public sealed class AccessibilityNode
{
    public required string Role;
    public string? Name;
    public string? Value;
    public bool Focusable;
    public bool Focused;                  // has keyboard/edit focus right now
    public bool Disabled;
    public bool? Checked;                 // switch / checkbox / radio
    public bool? Selected;                // tab / option / treeitem (aria-selected)
    public bool? Expanded;                // aria-expanded, where the role carries it
    public double? Now, Min, Max;         // slider / progressbar / spinbutton
    public (float X, float Y, float W, float H) Bounds;   // on-screen CSS px (scroll applied)

    /// <summary>True when the node's box lies entirely outside what is actually on screen — scrolled
    /// past, or clipped away by an <c>overflow</c> ancestor. It is still in the tree (an AT may
    /// legitimately want the whole document), but every bridge marks it so a screen reader does not
    /// stop on it. Without this a reader walks the entire document instead of the visible page: the
    /// Showcase's landing page alone carries 88 such controls.</summary>
    public bool Offscreen;

    public AccessibilityNode? Parent;
    public readonly List<AccessibilityNode> Children = new();

    /// <summary>Structural path of the backing render node (child-index chain from the root) — the
    /// identity that survives the per-keystroke rebuild, same scheme scroll restoration uses. Feed it
    /// to <see cref="CupriDocument.NodeAtPath"/> / the accessibility action methods.</summary>
    public string Path = "";

    /// <summary>Stable author-provided handle for AT clients: the element <c>id</c>, falling back to
    /// the binding path (<c>data-bind-value</c>/<c>data-bind-checked</c>). Null when anonymous.</summary>
    public string? AutomationId;

    /// <summary>The web platform's <c>autocomplete</c> token — <c>username</c>,
    /// <c>current-password</c>, <c>email</c>, <c>tel</c>, <c>name</c>, <c>postal-code</c>… What a
    /// password manager needs in order to know WHAT to fill. Null when the author didn't say, in
    /// which case nothing is offered: guessing a field is a password would be a security bug, not
    /// a convenience.</summary>
    public string? AutofillHint;
}

/// <summary>Builds the semantics tree from a laid-out render tree.</summary>
public static class AccessibilityTree
{
    public static AccessibilityNode Build(RenderNode root) => Build(root, null, null);

    /// <summary>
    /// Build with the document's own oracles: <paramref name="isFocusable"/> is the SAME predicate Tab
    /// order uses (so "focusable" here never disagrees with where Tab actually stops), and
    /// <paramref name="focused"/> is the render node that currently holds keyboard/edit focus.
    /// </summary>
    public static AccessibilityNode Build(RenderNode root, Func<IElement, bool>? isFocusable, RenderNode? focused)
    {
        var node = new AccessibilityNode { Role = "document", Bounds = (root.X, root.Y, root.Width, root.Height) };
        // The viewport is the outermost clip: anything landing outside it is off screen by
        // definition, and everything inside narrows from here.
        var viewport = (root.X, root.Y, root.Width, root.Height);
        for (var i = 0; i < root.Children.Count; i++)
            Collect(root.Children[i], ChildOriginX(root, root.X), ChildOriginY(root, root.Y), "/" + i, node, isFocusable, focused, viewport, viewport);
        return node;
    }

    // Children of a scrolled element are shifted up by the clamped scroll offset — the same
    // correction HitTesting.Hit applies, so a click synthesized at an a11y node's centre lands
    // on that node even inside a scrolled container.
    private static float ChildOriginY(RenderNode n, float ay) =>
        ay - (n.IsScrollable ? Math.Clamp(n.ScrollY, 0, n.MaxScrollY) : 0f);

    /// <summary>The same correction on the horizontal axis, so a control dragged into view from a
    /// sideways-scrolling row reports where it now IS — a screen reader tapping the rectangle it
    /// was given has to land on the control.</summary>
    private static float ChildOriginX(RenderNode n, float ax) =>
        ax - (n.IsScrollableX ? n.ClampedScrollX : 0f);

    private static void Collect(RenderNode render, float originX, float originY, string path,
        AccessibilityNode parent, Func<IElement, bool>? isFocusable, RenderNode? focused,
        (float X, float Y, float W, float H) clip, (float X, float Y, float W, float H) viewport)
    {
        if (render.Style.Display == DisplayType.None) return;

        // Top-layer nodes (overlays, position:fixed) already hold absolute viewport coordinates.
        var ax = (render.IsTopLayer ? 0 : originX) + render.X;
        var ay = (render.IsTopLayer ? 0 : originY) + render.Y;
        // ...and they escape their ancestors' clipping too, which is the point of the top layer.
        if (render.IsTopLayer) clip = viewport;

        var role = RoleOf(render);
        var target = parent;
        if (role is not null && render.Element is { } el)
        {
            var bounds = InlineAwareBounds(render, ax, ay);
            var sem = new AccessibilityNode
            {
                Role = role,
                Name = AccessibleName(render, el, role),
                Path = path,
                Parent = parent,
                Bounds = bounds,
                Focusable = isFocusable?.Invoke(el)
                            ?? role is "slider" or "button" or "switch" or "checkbox" or "radio"
                                    or "link" or "textbox" or "spinbutton",
                Focused = focused is not null && ReferenceEquals(render, focused),
                Disabled = IsDisabled(el),
                AutomationId = FirstAttr(el, "id", "data-bind-value", "data-bind-checked"),
                AutofillHint = AutofillHintFor(el),
                Offscreen = !Intersects(clip, bounds.X, bounds.Y, bounds.W, bounds.H),
            };
            ApplyValues(sem, render, el, role);
            parent.Children.Add(sem);
            target = sem;
        }
        // A node whose overflow is not `visible` clips its children — the SAME condition the painter
        // uses to emit a PushClip, so what paints clipped and what reads as off screen cannot
        // disagree.
        var childClip = clip;
        if (render.Style.Overflow != OverflowMode.Visible)
            childClip = Intersect(clip,
                ax + render.BorderLeftW, ay + render.BorderTopW,
                render.Width - render.BorderLeftW - render.BorderRightW,
                render.Height - render.BorderTopW - render.BorderBottomW);

        var childOy = ChildOriginY(render, ay);
        for (var i = 0; i < render.Children.Count; i++)
            Collect(render.Children[i], ChildOriginX(render, ax), childOy, path + "/" + i, target, isFocusable, focused, childClip, viewport);
    }

    private static (float X, float Y, float W, float H) Intersect(
        (float X, float Y, float W, float H) a, float x, float y, float w, float h)
    {
        var left = Math.Max(a.X, x);
        var top = Math.Max(a.Y, y);
        var right = Math.Min(a.X + a.W, x + w);
        var bottom = Math.Min(a.Y + a.H, y + h);
        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Any overlap at all counts as on screen: a control half scrolled into view is one the
    /// user can see and reach, so a strict containment test would hide it.</summary>
    private static bool Intersects((float X, float Y, float W, float H) clip, float x, float y, float w, float h) =>
        w > 0 && h > 0 && clip.W > 0 && clip.H > 0 &&
        x < clip.X + clip.W && x + w > clip.X && y < clip.Y + clip.H && y + h > clip.Y;

    /// <summary>The engine-wide definition of "disabled": the <c>disabled</c> attribute, the
    /// <c>disabled</c> class the components emit (e.g. a pagination arrow on the first page), or
    /// <c>aria-disabled</c>. The cursor logic and this tree share it so they can never disagree.</summary>
    public static bool IsDisabled(IElement el) =>
        el.ClassList.Contains("disabled")
        || el.HasAttribute("disabled")
        || el.GetAttribute("aria-disabled") is "true";

    /// <summary>An inline element (a link inside a paragraph) has no box of its own — layout zeroes
    /// it and positions its text through fragments — so reporting <c>render.Width/Height</c> would
    /// hand assistive technology an empty rectangle: nothing to announce a position for, nothing to
    /// tap. Fall back to the union of the text it actually occupies.</summary>
    private static (float X, float Y, float W, float H) InlineAwareBounds(RenderNode render, float ax, float ay)
    {
        if (render.Width > 0.01f && render.Height > 0.01f) return (ax, ay, render.Width, render.Height);

        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        void Union(float x, float y, float w, float h)
        {
            if (w <= 0 || h <= 0) return;
            l = MathF.Min(l, x); t = MathF.Min(t, y);
            r = MathF.Max(r, x + w); b = MathF.Max(b, y + h);
        }
        // Fragment coordinates are relative to the block that established the inline formatting
        // context; the zeroed boxes in between make (ax, ay) that block's origin already.
        void Walk(RenderNode n)
        {
            if (n.InlineFragments is { } frags) foreach (var f in frags) Union(ax + f.X, ay + f.Y, f.W, f.H);
            if (n.Lines is { } lines) foreach (var ln in lines) Union(ax + ln.X, ay + ln.Y, ln.Width, ln.Height);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(render);
        return r > l && b > t ? (l, t, r - l, b - t) : (ax, ay, render.Width, render.Height);
    }

    /// <summary>The author's <c>autocomplete</c>, found on the element or on the custom element it
    /// came from. A component expands into inner markup — the <c>role="textbox"</c> ends up on a
    /// child of <c>&lt;cupri-password&gt;</c>, while the attribute the author wrote stays on the
    /// custom element — so looking only at the node itself finds the hint on a plain field and
    /// misses it on every component, which is precisely the wrong half.</summary>
    private static string? AutofillHintFor(IElement el)
    {
        for (var e = el; e is not null; e = e.ParentElement)
        {
            if (e.GetAttribute("autocomplete") is { Length: > 0 } hint) return hint;
            if (e.LocalName is "body" or "form") break;      // don't inherit across the whole page
        }
        return null;
    }

    private static string? FirstAttr(IElement el, params string[] names)
    {
        foreach (var name in names)
            if (el.GetAttribute(name) is { Length: > 0 } v) return v;
        return null;
    }

    private static string? RoleOf(RenderNode n)
    {
        var explicitRole = n.Element?.GetAttribute("role");
        if (explicitRole is { Length: > 0 }) return explicitRole;
        return n.Tag switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "heading",
            "a" => "link",
            "button" => "button",
            "img" => "image",
            _ => null,
        };
    }

    private static void ApplyValues(AccessibilityNode sem, RenderNode render, IElement el, string role)
    {
        double? Attr(string name) =>
            double.TryParse(el.GetAttribute(name), CultureInfo.InvariantCulture, out var v) ? v : null;

        switch (role)
        {
            case "slider" or "progressbar" or "spinbutton":
                sem.Now = Attr("aria-valuenow");
                sem.Min = Attr("aria-valuemin");
                sem.Max = Attr("aria-valuemax");
                sem.Value = sem.Now?.ToString(CultureInfo.InvariantCulture);
                break;
            case "switch" or "checkbox" or "radio":
                sem.Checked = el.GetAttribute("aria-checked") == "true";
                break;
            case "textbox" or "combobox":
                if (role == "textbox" && !el.HasAttribute("data-mask") && !el.HasAttribute("data-multiline")
                    && el.QuerySelector("[data-caret-anchor]") is { } anchor)
                {
                    // Single-line fields keep their actual editor text in this
                    // anchor. Render runs omit whitespace-only nodes; trimming
                    // them also changes valid values. Placeholders are hints.
                    sem.Value = anchor.ClassList.Contains("cupri-tf-ph") ? "" : anchor.TextContent;
                    break;
                }
                // The rendered text IS the live value (the per-keystroke rebuild writes the edit
                // buffer into the DOM, masked for passwords — so this never leaks one).
                var text = CollectText(render).Trim();
                if (text.Length > 0) sem.Value = text;
                break;
        }

        if (el.GetAttribute("aria-selected") is ("true" or "false") and var sel) sem.Selected = sel == "true";
        if (el.GetAttribute("aria-expanded") is ("true" or "false") and var exp) sem.Expanded = exp == "true";
    }

    private static string? AccessibleName(RenderNode render, IElement el, string role)
    {
        var label = el.GetAttribute("aria-label");
        if (label is { Length: > 0 }) return label;

        // CONTAINERS never take their name from descendant text: a virtualised list's "name"
        // would be every materialised row concatenated — twenty rows read aloud on focus, and
        // (found the hard way) a blob that satisfies text searches its CHILDREN should answer.
        // Containers are named by aria-label or not at all; their children carry the content.
        if (role is "list" or "listbox" or "menu" or "menubar" or "tablist" or "tree" or "grid"
                 or "table" or "radiogroup" or "group" or "toolbar" or "tabpanel" or "dialog")
            return null;

        var text = CollectText(render).Trim();
        if (text.Length > 0) return text;

        // Positional label — the SAME association a click on the label uses (LabelTargets,
        // inverted), so what a screen reader announces and what the click activates never
        // disagree. Prefer the text after the control ("[box] Label"), then before it
        // ("Label [switch]"); a labelable sibling is a fellow control, not a label.
        if (role is "switch" or "checkbox" or "radio")
        {
            if (el.NextElementSibling is { } next && !IsLabelable(next)
                && next.TextContent.Trim() is { Length: > 0 } after) return after;
            if (el.PreviousElementSibling is { } prev && !IsLabelable(prev)
                && prev.TextContent.Trim() is { Length: > 0 } before) return before;
        }
        return null;
    }

    private static bool IsLabelable(IElement el) =>
        el.GetAttribute("role") is "switch" or "checkbox" or "radio";

    private static string CollectText(RenderNode n)
    {
        if (n.IsText) return (n.Text ?? "") + " ";
        var sb = new StringBuilder();
        foreach (var c in n.Children) sb.Append(CollectText(c));
        return sb.ToString();
    }

    /// <summary>Human-readable dump for verification (mirrors what a screen reader sees).</summary>
    public static string Dump(AccessibilityNode node, int depth = 0)
    {
        var sb = new StringBuilder();
        var indent = new string(' ', depth * 2);
        var parts = new List<string> { node.Role };
        if (node.Name is { Length: > 0 }) parts.Add($"\"{node.Name}\"");
        if (node.Checked is { } isChecked) parts.Add($"checked={isChecked}");
        if (node.Selected is { } isSelected) parts.Add($"selected={isSelected}");
        if (node.Expanded is { } isExpanded) parts.Add($"expanded={isExpanded}");
        if (node.Now is { } now) parts.Add($"value={now}{(node.Min is { } mn ? $" [{mn}..{node.Max}]" : "")}");
        if (node.Focusable) parts.Add("focusable");
        if (node.Focused) parts.Add("FOCUSED");
        if (node.Disabled) parts.Add("disabled");
        sb.AppendLine($"{indent}{string.Join(' ', parts)}");
        foreach (var child in node.Children) sb.Append(Dump(child, depth + 1));
        return sb.ToString();
    }
}
