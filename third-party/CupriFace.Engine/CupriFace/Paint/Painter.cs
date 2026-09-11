using AngleSharp.Dom;
using CupriFace.Dom;
using CupriFace.Style;
using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// Walks the laid-out render tree and builds an immutable <see cref="DisplayList"/> of
/// absolute-positioned paint commands. Pure data-in/data-out — no Skia here, so it can
/// run on the UI thread and hand the snapshot to the rasteriser.
/// </summary>
public sealed class Painter
{
    private readonly ImageStore? _images;
    private readonly SurfaceRegistry? _surfaces;
    public Painter(ImageStore? images = null, SurfaceRegistry? surfaces = null)
    { _images = images; _surfaces = surfaces; }

    /// <summary>Dev overlay: outline every element's border box (scrollers in a second colour) on top
    /// of the normal paint. Toggled via <c>CupriDocument.DebugOverlay</c>.</summary>
    public bool DebugOutline;

    // The lifted reorder/kanban card, deferred to a global layer painted last (over other columns and any
    // content below the board) — a per-container defer would leave it behind whatever paints after its
    // column. Node + the origin it was reached at; reset each Build.
    private RenderNode? _dragCard;
    private float _dragOx, _dragOy;

    private const float CullMargin = 60f; // paint a little past the viewport so scrolling never flashes blank
    private static readonly SKColor _dbgBox = new(0xE0, 0x2F, 0x8A, 0x66);    // magenta box outline
    private static readonly SKColor _dbgScroll = new(0x2F, 0x8A, 0xE0, 0x99); // blue for scroll containers

    private static ObjectFit ParseFit(string? v) => v switch
    {
        "cover" => ObjectFit.Cover,
        "fill" => ObjectFit.Fill,
        "none" => ObjectFit.None,
        _ => ObjectFit.Contain,
    };

    public DisplayList Build(RenderNode root)
    {
        var list = new DisplayList();
        var topLayer = new List<RenderNode>();
        _dragCard = null;

        // backdrop-filter on a top-layer scrim (a modal/drawer/shelf) blurs the page BEHIND it. Skia has
        // no backdrop-capture we can reach, but the top layer paints last — so we blur the whole
        // background as one group (the main content AND any OTHER open overlay, e.g. a pinned tooltip),
        // then paint the modal's own scrim + panel sharp on top. Blurring the other overlays too is what
        // keeps them from poking through the frost; the modal owns everything under its container element.
        var backdropNode = FindBackdropNode(root);
        var modalContainer = backdropNode?.Element?.ParentElement;

        // A backdrop filter blurs the whole page behind it, so its layer must span the viewport — pass
        // W/H ≤ 0 to leave it unbounded (the whole clip).
        if (backdropNode is not null) list.Add(new PushFilter(backdropNode.Style.BackdropFilter!, 0, 0, 0, 0));
        PaintNode(list, root, 0, 0, topLayer, inTopLayer: false);

        // Overlays paint last (above everything), ordered by z-index. Their X/Y are already absolute
        // viewport coordinates, so origin is (0,0).
        var ordered = topLayer.OrderBy(n => n.Style.ZIndex).ToList();
        if (backdropNode is not null && modalContainer is not null)
        {
            foreach (var o in ordered.Where(n => !IsUnder(n, modalContainer))) // background overlays → blurred
                PaintNode(list, o, 0, 0, topLayer, inTopLayer: true);
            list.Add(new PopFilter());
            foreach (var o in ordered.Where(n => IsUnder(n, modalContainer)))  // the modal itself → sharp, on top
                PaintNode(list, o, 0, 0, topLayer, inTopLayer: true);
        }
        else
        {
            if (backdropNode is not null) list.Add(new PopFilter());
            foreach (var o in ordered)
                PaintNode(list, o, 0, 0, topLayer, inTopLayer: true);
        }

        // The lifted card floats above everything — its shadow, then the card at its dragged offset.
        if (_dragCard is { } d)
        {
            var dx = _dragOx + d.X + d.DragOffsetX;
            var dy = _dragOy + d.Y + d.DragOffsetY;
            list.Add(new ShadowRect(dx, dy, d.Width, d.Height, d.Style.BorderRadius, 0, 4, 16, 0, new SKColor(0, 0, 0, 0x33), false));
            PaintNode(list, d, _dragOx, _dragOy, topLayer, inTopLayer: false);
        }
        return list;
    }

    // How far a filter's result spreads beyond the element's box (blur halo, shadow offset+blur), so the
    // bounded layer doesn't clip it. Colour-matrix ops (grayscale/…) don't spread.
    private static float FilterMargin(IReadOnlyList<FilterOp> ops)
    {
        var m = 0f;
        foreach (var op in ops)
            m = op.Kind switch
            {
                FilterKind.Blur => MathF.Max(m, op.A * 3f),
                FilterKind.DropShadow => MathF.Max(m, MathF.Abs(op.A) + MathF.Abs(op.B) + op.C * 3f),
                _ => m,
            };
        return m;
    }

    // The first top-layer (fixed) element that requests a backdrop-filter — its filter blurs the page
    // behind it. Only top-layer scrims qualify (a full-viewport backdrop over the whole page).
    private static RenderNode? FindBackdropNode(RenderNode n)
    {
        if (n.IsTopLayer && n.Style.BackdropFilter is { Count: > 0 }) return n;
        foreach (var c in n.Children)
            if (FindBackdropNode(c) is { } found) return found;
        return null;
    }

    // Parse "x0,y0 x1,y1 …" normalised (0..1, y=0 top) chart points into a flat absolute [x,y,…] list
    // scaled into the (x,y,w,h) content box.
    private static List<float> ParsePoints(string data, float x, float y, float w, float h)
    {
        var pts = new List<float>();
        foreach (var pair in data.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var c = pair.Split(',');
            if (c.Length == 2
                && float.TryParse(c[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var nx)
                && float.TryParse(c[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ny))
            {
                pts.Add(x + nx * w);
                pts.Add(y + ny * h);
            }
        }
        return pts;
    }

    // Is <paramref name="container"/> an ancestor-or-self of node's element? Used to tell the modal's own
    // parts (scrim, panel, a popup opened inside it) from unrelated background overlays.
    private static bool IsUnder(RenderNode node, IElement container)
    {
        for (var e = node.Element; e is not null; e = e.ParentElement)
            if (ReferenceEquals(e, container)) return true;
        return false;
    }

    private void PaintNode(DisplayList list, RenderNode node, float originX, float originY, List<RenderNode> topLayer, bool inTopLayer,
        List<StickyItem>? stickyCollect = null, float scrollTop = float.NegativeInfinity)
    {
        // Lift a top-layer (fixed) node out of the normal walk; paint it in the deferred pass.
        if (!inTopLayer && node.IsTopLayer)
        {
            topLayer.Add(node);
            return;
        }

        // A position:sticky node inside a scroll container is deferred to a pass after the container's
        // normal content — so a stuck header paints ON TOP of what scrolls under it. Record where it is.
        if (stickyCollect is not null && node.Style.Position == PositionType.Sticky)
        {
            stickyCollect.Add(new StickyItem(node, originX, originY));
            return;
        }

        // node.X/Y are relative to the parent's border-box origin. DragOffsetX/Y shift a reorder item
        // (and its subtree) at paint time while it's being dragged / making room for the dragged one.
        var absX = originX + node.X + node.DragOffsetX;
        var absY = originY + node.Y + node.DragOffsetY;
        var s = node.Style;

        if (node.IsText)
        {
            PaintText(list, node, absX, absY);
            return;
        }

        // Filter wraps the whole subtree (outermost — the filter sees the composited element). The layer
        // is bounded to the element's box grown by the filter's spread, so the offscreen stays small.
        var filtered = s.Filter is { Count: > 0 };
        if (filtered)
        {
            var m = FilterMargin(s.Filter!);
            list.Add(new PushFilter(s.Filter!, absX - m, absY - m, node.Width + 2 * m, node.Height + 2 * m));
        }

        // Opacity composites the whole subtree as a group (wrapping any transform).
        var faded = s.Opacity < 1f;
        if (faded) list.Add(new PushOpacity(Math.Clamp(s.Opacity, 0f, 1f), absX, absY, node.Width, node.Height));

        // Transform wraps the node's whole subtree, applied around transform-origin — resolved
        // against the BORDER box, which is what the origin's percentages refer to. The initial
        // value is 50% 50%, so an element that says nothing still turns about its centre.
        var transformed = s.HasTransform;
        if (transformed)
        {
            var (pivotX, pivotY) = s.TransformPivot(node.Width, node.Height);
            list.Add(new PushTransform(
                absX + pivotX, absY + pivotY,
                s.TranslateX, s.TranslateY, s.ScaleX, s.ScaleY, s.RotateDeg));
        }

        // Box shadow: outset (drop) shadows paint BEHIND the background.
        if (s.BoxShadow is { Count: > 0 } shadows)
            foreach (var sh in shadows)
                if (!sh.Inset)
                    list.Add(new ShadowRect(absX, absY, node.Width, node.Height, s.BorderRadius,
                        sh.Dx, sh.Dy, sh.Blur, sh.Spread, sh.Color, false));

        // Background (fills the border box; drawn under the border).
        if (s.Background.Alpha > 0 && node.Width > 0)
            list.Add(new FillRect(absX, absY, node.Width, node.Height, s.BorderRadius, s.Background));

        // Background gradient (CSS linear-/radial-gradient), painted over any solid background colour.
        if (s.BackgroundGradient is { } grad && node.Width > 0)
            list.Add(new GradientRect(absX, absY, node.Width, node.Height, s.BorderRadius, grad));

        // Border frame.
        var hasBorder = (node.BorderTopW + node.BorderRightW + node.BorderBottomW + node.BorderLeftW) > 0
                        && s.BorderColor.Alpha > 0 && s.BorderStyle != BorderLineStyle.None;
        if (hasBorder && node.Width > 0)
            list.Add(new BorderRect(absX, absY, node.Width, node.Height, s.BorderRadius,
                node.BorderTopW, node.BorderRightW, node.BorderBottomW, node.BorderLeftW, s.BorderColor, s.BorderStyle));

        // Inline element with a background/border (a <code> chip): one rounded box per line it spans
        // (Width is 0 — a passthrough inline box), painted behind its text. Coords are in the block's
        // content box, i.e. relative to the same origin the element's text fragments use.
        if (node.InlineFragments is { Count: > 0 } inlineBoxes)
            foreach (var f in inlineBoxes)
            {
                if (s.Background.Alpha > 0)
                    list.Add(new FillRect(absX + f.X, absY + f.Y, f.W, f.H, s.BorderRadius, s.Background));
                if (s.BackgroundGradient is { } g)
                    list.Add(new GradientRect(absX + f.X, absY + f.Y, f.W, f.H, s.BorderRadius, g));
                if (hasBorder)
                    list.Add(new BorderRect(absX + f.X, absY + f.Y, f.W, f.H, s.BorderRadius,
                        node.BorderTopW, node.BorderRightW, node.BorderBottomW, node.BorderLeftW, s.BorderColor, s.BorderStyle));
            }

        // Box shadow: inset (inner) shadows paint on top of the background, clipped inside the box.
        if (s.BoxShadow is { Count: > 0 } insetShadows)
            foreach (var sh in insetShadows)
                if (sh.Inset)
                    list.Add(new ShadowRect(absX, absY, node.Width, node.Height, s.BorderRadius,
                        sh.Dx, sh.Dy, sh.Blur, sh.Spread, sh.Color, true));

        // Icon: fill an SVG path in the content box with the current color.
        if (node.IconPath is { Length: > 0 } iconPath)
        {
            var iw = node.Width - node.HorizontalInsets;
            var ih = node.Height - node.VerticalInsets;
            list.Add(new FillPath(absX + node.ContentLeftInset, absY + node.ContentTopInset, iw, ih, 24f, iconPath, s.Color));
        }

        // Live surface (video, future 3D viewports): the current frame, if one exists. Falls
        // through to ImageSrc otherwise — which is exactly how a poster shows until the first
        // frame arrives. Same DrawImage command, so object-fit/radius/damage all behave alike.
        // A HOST-COMPOSITED surface (web underlay video) paints no frames at all: punch a
        // transparent hole so the host's own element shows through; later paint stays on top.
        SKImage? frame = null;
        var surfaced = false; // a hole was punched OR a live surface command was emitted
        if (node.SurfaceKey is { Length: > 0 } surfaceKey && _surfaces?.Get(surfaceKey) is { } source)
        {
            if (source.HostComposited)
            {
                surfaced = true; // the poster must not paint into it — the underlay is the picture now
                list.Add(new ClearHole(
                    absX + node.ContentLeftInset, absY + node.ContentTopInset,
                    node.Width - node.HorizontalInsets, node.Height - node.VerticalInsets, s.BorderRadius));
            }
            else if (source.CurrentFrame is not null)
            {
                // Frames flow: emit a raster-time-resolved DrawSurface (NOT a captured DrawImage),
                // so a new frame leaves the display list unchanged — the surface fast path.
                surfaced = true;
                list.Add(new DrawSurface(
                    absX + node.ContentLeftInset, absY + node.ContentTopInset,
                    node.Width - node.HorizontalInsets, node.Height - node.VerticalInsets,
                    source, ParseFit(node.Element?.GetAttribute("data-object-fit")), s.BorderRadius));
            }
        }

        // Image: decode + draw into the content box, fitted per object-fit.
        if (frame is null && !surfaced && node.ImageSrc is { Length: > 0 } imageSrc) frame = _images?.Get(imageSrc);
        if (frame is { } img)
            list.Add(new DrawImage(
                absX + node.ContentLeftInset, absY + node.ContentTopInset,
                node.Width - node.HorizontalInsets, node.Height - node.VerticalInsets,
                img, ParseFit(node.Element?.GetAttribute("data-object-fit")), s.BorderRadius));

        // Clip children if overflow is not visible.
        var clip = s.Overflow != OverflowMode.Visible;
        if (clip)
            list.Add(new PushClip(absX + node.BorderLeftW, absY + node.BorderTopW,
                node.Width - node.BorderLeftW - node.BorderRightW,
                node.Height - node.BorderTopW - node.BorderBottomW, s.BorderRadius));

        // Chart line (line / sparkline / rolling): a polyline through normalised points scaled into the
        // content box, with an optional area fill (data-cupri-area) and dots (data-cupri-dots). Emitted
        // inside the clip so a plot with overflow:hidden crops the line to its (rounded) box.
        if (node.ChartLine is { Length: > 0 } chartLine)
        {
            var cx = absX + node.ContentLeftInset;
            var cy = absY + node.ContentTopInset;
            var cw = node.Width - node.HorizontalInsets;
            var ch = node.Height - node.VerticalInsets;
            var pts = ParsePoints(chartLine, cx, cy, cw, ch);
            if (pts.Count >= 4)
            {
                var el = node.Element;
                var lineW = CssNumber.TryParse(el?.GetAttribute("data-cupri-width"), out var lw) ? lw : 2f;
                var fillCol = el?.HasAttribute("data-cupri-area") == true
                    ? new SKColor(s.Color.Red, s.Color.Green, s.Color.Blue, 0x2E) : SKColor.Empty;
                var curved = el?.HasAttribute("data-cupri-curve") == true;
                list.Add(new Polyline(pts, lineW, s.Color, fillCol, cy + ch, curved));
                if (el?.HasAttribute("data-cupri-dots") == true)
                {
                    var r = lineW + 1.5f;
                    for (var i = 0; i + 1 < pts.Count; i += 2)
                        list.Add(new FillRect(pts[i] - r, pts[i + 1] - r, r * 2f, r * 2f, r, s.Color));
                }
            }
        }

        // Scroll: shift children up by the (clamped) vertical offset, and left by the horizontal
        // caret-follow offset (single-line fields). Both are computed before paint.
        var scrollY = 0f;
        if (node.IsScrollable)
        {
            scrollY = Math.Clamp(node.ScrollY, 0, node.MaxScrollY);
            node.ScrollY = scrollY;
            scrollY += node.OverscrollY;      // the rubber band, while a finger is stretching it
        }
        // Clamped for a container that overflows horizontally; passed through untouched for a
        // single-line text field, which owns its own caret-follow shift.
        var scrollX = node.ClampedScrollX;
        if (node.IsScrollableX) { node.ScrollX = scrollX; scrollX += node.OverscrollX; }

        // Virtualisation: in a scroll container, skip painting children whose box is entirely outside
        // the visible band (plus a margin). Long lists then cost paint+raster for the visible rows
        // only, not every row — the win during scrolling. Layout is unaffected (culling is paint-only).
        var cull = node.IsScrollable;
        var bandTop = node.ContentTopInset + scrollY - CullMargin;
        var bandBottom = node.ContentTopInset + scrollY + node.ContentBoxHeight + CullMargin;

        // A scroll container collects the sticky nodes in its subtree; they paint in a deferred pass below
        // (sticking to the top of its content box). A non-scroll node just passes the collector through.
        var stickyOwn = node.IsScrollable ? new List<StickyItem>() : null;
        var childSticky = node.IsScrollable ? stickyOwn : stickyCollect;
        // Sticky pins to the scrollport (padding-box) top, not the content box — content scrolls under the
        // padding, so a `top:0` header sits flush at the very top and covers it.
        var childScrollTop = node.IsScrollable ? absY + node.BorderTopW : scrollTop;

        RenderNode? dragged = null; // the lifted reorder item — painted last so it sits on top of its siblings
        foreach (var child in node.Children)
        {
            if (child.Style.Display == DisplayType.None) continue;
            if (child.Dragging) { dragged = child; continue; }
            // Sticky children are never culled — a stuck header's natural box may be scrolled out of band.
            if (cull && !child.IsTopLayer && child.Style.Position != PositionType.Sticky
                && (child.Y + child.Height < bandTop || child.Y > bandBottom)) continue;
            PaintNode(list, child, absX - scrollX, absY - scrollY, topLayer, inTopLayer, childSticky, childScrollTop);
        }
        // Defer the lifted card to a single global layer (painted after everything, incl. other columns and
        // whatever sits below the board), so it floats on top instead of hiding behind a later-painted sibling.
        if (dragged is not null) { _dragCard = dragged; _dragOx = absX - scrollX; _dragOy = absY - scrollY; }

        // Sticky pass: each deferred sticky node paints at its stuck position (clamped to its containing
        // block), on top of the scrolled content but still inside this container's clip.
        if (stickyOwn is { Count: > 0 })
            foreach (var it in stickyOwn)
                PaintSticky(list, it, childScrollTop, topLayer, inTopLayer);

        if (clip) list.Add(new PopClip());

        // Scrollbar thumb (on top of content, inside the padding box).
        if (node.IsScrollable)
        {
            var boxH = node.ContentBoxHeight;
            var thumbH = MathF.Max(28f, boxH * boxH / node.ScrollContentHeight);
            var thumbY = absY + node.ContentTopInset + scrollY / node.MaxScrollY * (boxH - thumbH);
            var thumbX = absX + node.Width - node.BorderRightW - 8f;
            list.Add(new FillRect(thumbX, thumbY, 5f, thumbH, 2.5f, new SKColor(0x60, 0x6a, 0x7a, 0xB0)));
        }

        // Resize grip (CSS resize) in the bottom-right corner.
        if (s.Resize != ResizeMode.None)
        {
            const float grip = 13f;
            list.Add(new ResizeGrip(
                absX + node.Width - node.BorderRightW - grip - 2f,
                absY + node.Height - node.BorderBottomW - grip - 2f,
                grip, new SKColor(0x8b, 0x93, 0xa7)));
        }

        // Debug overlay: outline this element's border box on top of its content.
        if (DebugOutline && node.Width > 0 && node.Height > 0)
            list.Add(new BorderRect(absX, absY, node.Width, node.Height, 0f, 1, 1, 1, 1,
                node.IsScrollable ? _dbgScroll : _dbgBox));

        if (transformed) list.Add(new PopTransform());
        if (faded) list.Add(new PopOpacity());
        if (filtered) list.Add(new PopFilter());
    }

    // A collected position:sticky node and the origin it was reached at (its parent's painted top-left).
    private readonly record struct StickyItem(RenderNode Node, float OriginX, float OriginY);

    // Paint a sticky node at its stuck position: it sticks `top` px below the scroll container's content
    // top, but never scrolls above its natural place, and never past its containing block's bottom (so it
    // rides out with the parent). scrollTop is the container's absolute content-box top.
    private void PaintSticky(DisplayList list, StickyItem it, float scrollTop, List<RenderNode> topLayer, bool inTopLayer)
    {
        var n = it.Node;
        var natural = it.OriginY + n.Y;                                     // where the node scrolled to
        var top = n.Style.Top.IsDefinite ? n.Style.Top.Resolve(0f) : 0f;
        var parentBottom = it.OriginY + (n.Parent?.Height ?? n.Height);     // its containing block's bottom
        var stuck = MathF.Min(MathF.Max(natural, scrollTop + top), parentBottom - n.Height);
        PaintNode(list, n, it.OriginX, it.OriginY + (stuck - natural), topLayer, inTopLayer);
    }

    private static void PaintText(DisplayList list, RenderNode node, float absX, float absY)
    {
        if (node.Lines is null) return;
        var s = node.Style;
        foreach (var line in node.Lines)
        {
            if (line.Text.Length == 0) continue;
            list.Add(new TextRun(
                X: absX + line.X, Y: absY + line.Y,
                ContainerWidth: node.Width, LineWidth: line.Width, LineHeight: line.Height,
                Text: line.Text, Family: s.FontFamily, Weight: s.FontWeight, Size: s.FontSize,
                Color: s.Color, Align: s.TextAlign, Slant: s.FontStyle, Decorations: s.Decorations));
        }
    }
}
