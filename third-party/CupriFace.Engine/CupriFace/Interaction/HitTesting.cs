using CupriFace.Dom;
using CupriFace.Style;

namespace CupriFace.Interaction;

/// <summary>
/// Point → node hit-testing over the laid-out render tree (Layer 0 → input). Mirrors the
/// painter: overlays (top-layer / position:fixed) are tested first, topmost z-index first,
/// then the main tree — so a dialog backdrop or dropdown correctly captures clicks.
/// </summary>
public static class HitTesting
{
    public static RenderNode? HitTest(RenderNode root, float x, float y)
    {
        // Overlays are on top: test them first, highest z-index first.
        var overlays = new List<RenderNode>();
        Collect(root, overlays);
        foreach (var overlay in overlays.OrderByDescending(n => n.Style.ZIndex))
        {
            var hit = Hit(overlay, 0, 0, x, y, inTopLayer: true);
            if (hit is not null) return hit;
        }
        // Then the normal content (top-layer subtrees are skipped).
        return Hit(root, 0, 0, x, y, inTopLayer: false);
    }

    private static void Collect(RenderNode node, List<RenderNode> overlays)
    {
        foreach (var child in node.Children)
        {
            if (child.IsTopLayer) overlays.Add(child);
            Collect(child, overlays);
        }
    }

    private static RenderNode? Hit(RenderNode node, float originX, float originY, float x, float y, bool inTopLayer)
    {
        if (node.Style.Display == DisplayType.None) return null;
        if (!inTopLayer && node.IsTopLayer) return null; // reached via the main pass — skip; tested in the overlay pass

        var ax = originX + node.X;
        var ay = originY + node.Y;

        // A transformed node PAINTS somewhere other than its layout box, so the pointer has to be
        // mapped into that box's space before anything is compared. Without this a scaled-up tile
        // can only be grabbed inside its original rectangle and a rotated one only near its
        // unrotated corners — it looks like the shape moved but its handle stayed behind. The
        // mapping is inherited by the subtree, because children paint through the same matrix.
        if (node.Style.HasTransform)
        {
            var s = node.Style;
            var (pivotX, pivotY) = s.TransformPivot(node.Width, node.Height);
            float cx = ax + pivotX, cy = ay + pivotY;
            var local = SkiaSharp.SKMatrix.CreateTranslation(cx + s.TranslateX, cy + s.TranslateY);
            local = local.PreConcat(SkiaSharp.SKMatrix.CreateRotationDegrees(s.RotateDeg));
            local = local.PreConcat(SkiaSharp.SKMatrix.CreateScale(s.ScaleX, s.ScaleY));
            local = local.PreConcat(SkiaSharp.SKMatrix.CreateTranslation(-cx, -cy));
            if (local.TryInvert(out var inverse))
            {
                var p = inverse.MapPoint(x, y);
                x = p.X;
                y = p.Y;
            }
        }

        var inside = x >= ax && x < ax + node.Width && y >= ay && y < ay + node.Height;

        RenderNode? best = inside && !node.IsText ? node : null;
        // Children of a horizontally scrolled box are shifted left by its offset, exactly as the
        // painter shifts them — otherwise a card dragged into view could not be tapped where it
        // now appears.
        var childOx = ax - (node.IsScrollableX ? node.EffectiveScrollX : 0f);

        // Inline content owns no box: LayoutInline zeroes an inline element's X/Y/W/H and positions
        // its text through fragments instead. Without this, a link inside a paragraph is invisible
        // to the pointer — it paints, it looks clickable, and every tap falls through to the
        // paragraph behind it. Fragment coordinates are relative to the block that established the
        // inline formatting context, and the zeroed boxes in between mean (ax, ay) is already that
        // block's origin, so they compose without special-casing the nesting depth.
        if (best is null)
        {
            if (node.InlineFragments is { } frags)
                foreach (var f in frags)
                    if (x >= ax + f.X && x < ax + f.X + f.W && y >= ay + f.Y && y < ay + f.Y + f.H)
                    { best = node; break; }

            // A text run answers for the element that owns it — the contract here is that a hit is
            // always an element, never a text node.
            if (best is null && node.IsText && node.Lines is { } lines && node.Parent is { } owner)
                foreach (var ln in lines)
                    if (x >= ax + ln.X && x < ax + ln.X + ln.Width && y >= ay + ln.Y && y < ay + ln.Y + ln.Height)
                    { best = owner; break; }
        }
        // Children of a scrolled element are shifted up by the scroll offset.
        var childOy = ay - (node.IsScrollable ? node.EffectiveScrollY : 0f);

        // Paint clips descendants to the padding box whenever overflow is not visible. Keep the
        // element itself hittable in its border, but never descend to a child at a point where that
        // child cannot be seen. Top-layer descendants are tested separately from the viewport and
        // therefore remain independent of this normal-tree clip, exactly as they are in Painter.
        if (node.Style.Overflow != OverflowMode.Visible && !InsideOverflowClip(node, ax, ay, x, y))
            return best;

        foreach (var child in node.Children)
        {
            var hit = Hit(child, childOx, childOy, x, y, inTopLayer);
            if (hit is not null) best = hit;
        }
        return best;
    }

    // Painter clips overflow to this same rounded padding box. The node itself still owns its
    // rectangular border box, but descendants in a visually cut-away corner must not receive input.
    private static bool InsideOverflowClip(RenderNode node, float ax, float ay, float x, float y)
    {
        var left = ax + node.BorderLeftW;
        var top = ay + node.BorderTopW;
        var right = ax + node.Width - node.BorderRightW;
        var bottom = ay + node.Height - node.BorderBottomW;
        if (x < left || x >= right || y < top || y >= bottom) return false;

        var radius = MathF.Min(MathF.Max(0, node.Style.BorderRadius),
            MathF.Min(right - left, bottom - top) / 2f);
        if (radius <= 0) return true;

        var dx = x < left + radius ? x - (left + radius)
            : x > right - radius ? x - (right - radius) : 0f;
        var dy = y < top + radius ? y - (top + radius)
            : y > bottom - radius ? y - (bottom - radius) : 0f;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>Absolute border-box of a node. Stops accumulating at a top-layer ancestor
    /// (whose X/Y is already absolute viewport coordinates).</summary>
    public static (float X, float Y, float W, float H) AbsoluteBox(RenderNode node)
    {
        float x = 0, y = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            x += n.X;
            y += n.Y;
            if (n.IsTopLayer) break;
        }
        return (x, y, node.Width, node.Height);
    }

    /// <summary>On-screen border-box: <see cref="AbsoluteBox"/> corrected for scrolled ancestors with
    /// exactly the shift <see cref="HitTest"/> applies — so a click synthesized at this box's centre
    /// lands on the node even inside a scrolled container. (AbsoluteBox is the box where the node
    /// WOULD be unscrolled; this is where it IS.)</summary>
    private static (float X, float Y) Origin(RenderNode node)
    {
        float x = 0, y = 0;
        for (var n = node; n is not null; n = n.Parent)
        {
            x += n.X;
            y += n.Y;
            if (n.IsTopLayer) break;
            if (n.Parent is { IsScrollable: true } p) y -= p.EffectiveScrollY;
            if (n.Parent is { IsScrollableX: true } px) x -= px.EffectiveScrollX;
        }
        return (x, y);
    }

    /// <summary>Where to aim a synthesised click at this node. The centre of the box, except for
    /// inline content: a link that WRAPS has a bounding box whose centre can land between its two
    /// lines — on the paragraph, not the link — so aim at its first fragment instead. (Found on
    /// Linux, where different font metrics wrapped a link that fitted on one line elsewhere.)</summary>
    public static (float X, float Y) ActivationPoint(RenderNode node)
    {
        var (x, y) = Origin(node);
        if (node.Width > 0.01f && node.Height > 0.01f)
            return (x + node.Width / 2, y + node.Height / 2);

        var first = FirstFragment(node);
        return first is { } f ? (x + f.X + f.W / 2, y + f.Y + f.H / 2)
                              : (x + node.Width / 2, y + node.Height / 2);
    }

    private static InlineRect? FirstFragment(RenderNode node)
    {
        if (node.InlineFragments is { Count: > 0 } frags) return frags[0];
        if (node.Lines is { Count: > 0 } lines)
            return new InlineRect(lines[0].X, lines[0].Y, lines[0].Width, lines[0].Height);
        foreach (var c in node.Children) if (FirstFragment(c) is { } f) return f;
        return null;
    }

    public static (float X, float Y, float W, float H) ScreenBox(RenderNode node)
    {
        var (x, y) = Origin(node);
        if (node.Width > 0.01f && node.Height > 0.01f) return (x, y, node.Width, node.Height);

        // Inline content is positioned through fragments, not a box (LayoutInline zeroes it), so
        // the honest answer for a link inside a paragraph is the union of the text it occupies.
        // Everything that aims at a node's centre — synthesised clicks, screen-reader activation,
        // the a11y bridges' bounds — would otherwise aim at an empty point beside the words.
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        void Union(float ux, float uy, float uw, float uh)
        {
            if (uw <= 0 || uh <= 0) return;
            l = MathF.Min(l, ux); t = MathF.Min(t, uy);
            r = MathF.Max(r, ux + uw); b = MathF.Max(b, uy + uh);
        }
        void Walk(RenderNode n)
        {
            if (n.InlineFragments is { } frags) foreach (var f in frags) Union(x + f.X, y + f.Y, f.W, f.H);
            if (n.Lines is { } lines) foreach (var ln in lines) Union(x + ln.X, y + ln.Y, ln.Width, ln.Height);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(node);
        return r > l && b > t ? (l, t, r - l, b - t) : (x, y, node.Width, node.Height);
    }

    /// <summary>The accumulated CSS-transform matrix mapping this node's untransformed
    /// <see cref="ScreenBox"/> to where the rasteriser actually painted it — the same per-node
    /// matrices the paint path applies (centre = each transformed node's own box centre),
    /// composed outermost-first like the nested transform scopes. Identity when nothing on the
    /// chain is transformed. A host compositing an underlay under a painted hole (the web
    /// <c>&lt;video&gt;</c>) needs this: the HOLE paints through the transforms, so the element
    /// must follow the very same mapping or the two shear apart.</summary>
    public static SkiaSharp.SKMatrix ScreenTransform(RenderNode node)
    {
        var m = SkiaSharp.SKMatrix.Identity;
        Accumulate(node);
        return m;

        void Accumulate(RenderNode n)
        {
            if (n.Parent is { } parent) Accumulate(parent);   // outermost transform applies first
            var s = n.Style;
            if (!s.HasTransform) return;
            var (x, y, w, h) = ScreenBox(n);
            var (pivotX, pivotY) = s.TransformPivot(w, h);
            float cx = x + pivotX, cy = y + pivotY;
            var local = SkiaSharp.SKMatrix.CreateTranslation(cx + s.TranslateX, cy + s.TranslateY);
            local = local.PreConcat(SkiaSharp.SKMatrix.CreateRotationDegrees(s.RotateDeg));
            local = local.PreConcat(SkiaSharp.SKMatrix.CreateScale(s.ScaleX, s.ScaleY));
            local = local.PreConcat(SkiaSharp.SKMatrix.CreateTranslation(-cx, -cy));
            m = m.PreConcat(local);
        }
    }
}
