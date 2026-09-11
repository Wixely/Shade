using CupriFace.Style;
using CupriFace.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CupriFace.Paint;

/// <summary>
/// Plays an immutable <see cref="DisplayList"/> onto an <see cref="SKCanvas"/>. This is
/// the "render thread" side of the commit-snapshot seam: it reads only the snapshot and
/// the font cache, never the live render tree.
/// </summary>
public sealed class SkiaRasterizer
{
    private readonly FontService _fonts;
    public SkiaRasterizer(FontService fonts) => _fonts = fonts;

    private static readonly SKSamplingOptions _imageSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // Destination rect for an image inside its box per object-fit (always drawing the whole image;
    // the caller clips to the box, so Cover's overflow is cropped).
    private static SKRect FitRect(DrawImage di)
    {
        float bw = di.W, bh = di.H, iw = di.Image.Width, ih = di.Image.Height;
        if (iw <= 0 || ih <= 0 || di.Fit == ObjectFit.Fill) return new SKRect(di.X, di.Y, di.X + bw, di.Y + bh);
        var scale = di.Fit switch
        {
            ObjectFit.Cover => MathF.Max(bw / iw, bh / ih),
            ObjectFit.None => 1f,
            _ => MathF.Min(bw / iw, bh / ih), // Contain
        };
        float dw = iw * scale, dh = ih * scale;
        float x = di.X + (bw - dw) / 2f, y = di.Y + (bh - dh) / 2f; // centre in the box
        return new SKRect(x, y, x + dw, y + dh);
    }

    // Build a Skia gradient shader over the box (g.X,g.Y,g.W,g.H) from a CSS gradient.
    private static SKShader BuildGradient(GradientRect g)
    {
        var grad = g.Gradient;
        var colors = new SKColor[grad.Stops.Count];
        var pos = new float[grad.Stops.Count];
        var explicitPos = false;
        for (var i = 0; i < grad.Stops.Count; i++)
        {
            colors[i] = grad.Stops[i].Color;
            var p = grad.Stops[i].Position;
            if (float.IsNaN(p)) pos[i] = grad.Stops.Count == 1 ? 0f : (float)i / (grad.Stops.Count - 1);
            else { pos[i] = Math.Clamp(p, 0f, 1f); explicitPos = true; }
        }
        var positions = explicitPos ? pos : null; // even distribution when none are specified

        if (grad.Kind == GradientKind.Radial)
        {
            var center = new SKPoint(g.X + g.W / 2f, g.Y + g.H / 2f);
            var radius = MathF.Sqrt(g.W * g.W + g.H * g.H) / 2f; // reach the farthest corner
            return SKShader.CreateRadialGradient(center, MathF.Max(1f, radius), colors, positions, SKShaderTileMode.Clamp);
        }

        // Linear: the CSS gradient line through the centre at the angle (0=up, clockwise); its length
        // covers the box so the end stops sit on the far edges.
        var rad = grad.AngleDeg * MathF.PI / 180f;
        float dx = MathF.Sin(rad), dy = -MathF.Cos(rad);
        var len = MathF.Abs(g.W * dx) + MathF.Abs(g.H * dy);
        float cx = g.X + g.W / 2f, cy = g.Y + g.H / 2f;
        var start = new SKPoint(cx - dx * len / 2f, cy - dy * len / 2f);
        var end = new SKPoint(cx + dx * len / 2f, cy + dy * len / 2f);
        return SKShader.CreateLinearGradient(start, end, colors, positions, SKShaderTileMode.Clamp);
    }

    // Append a chart line to <paramref name="path"/> (already moved to point 0): straight segments, or a
    // smooth Catmull-Rom spline (each segment's cubic control points come from the neighbouring points).
    private static void AppendChartPath(SKPath path, IReadOnlyList<float> p, bool curved)
    {
        var n = p.Count / 2;
        if (!curved)
        {
            for (var i = 1; i < n; i++) path.LineTo(p[i * 2], p[i * 2 + 1]);
            return;
        }
        for (var i = 0; i < n - 1; i++)
        {
            int i0 = Math.Max(0, i - 1), i2 = i + 1, i3 = Math.Min(n - 1, i + 2);
            float p0x = p[i0 * 2], p0y = p[i0 * 2 + 1], p1x = p[i * 2], p1y = p[i * 2 + 1];
            float p2x = p[i2 * 2], p2y = p[i2 * 2 + 1], p3x = p[i3 * 2], p3y = p[i3 * 2 + 1];
            path.CubicTo(p1x + (p2x - p0x) / 6f, p1y + (p2y - p0y) / 6f,
                         p2x - (p3x - p1x) / 6f, p2y - (p3y - p1y) / 6f, p2x, p2y);
        }
    }

    // SaveLayer bounded to (x,y,w,h) — the element's box — so the offscreen is that size, not the whole
    // canvas. A non-positive width falls back to an unbounded layer (the whole clip), for a full-viewport
    // backdrop filter.
    private static void SaveLayer(SKCanvas canvas, SKPaint paint, float x, float y, float w, float h)
    {
        if (w > 0 && h > 0) canvas.SaveLayer(new SKRect(x, y, x + w, y + h), paint);
        else canvas.SaveLayer(paint);
    }

    public void Paint(SKCanvas canvas, DisplayList list) => Paint(canvas, list.Commands);

    /// <summary>Rasterise a command sequence — the full list, or a culled subset (the surface
    /// fast path replays only what intersects its damage; see <c>DamageDiff.CullTo</c>).</summary>
    public void Paint(SKCanvas canvas, IReadOnlyList<PaintCommand> commands)
    {
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        foreach (var cmd in commands)
        {
            switch (cmd)
            {
                case FillRect r:
                    fill.Color = r.Color;
                    DrawRect(canvas, fill, r.X, r.Y, r.W, r.H, r.Radius);
                    break;

                case GradientRect g:
                {
                    using var shader = BuildGradient(g);
                    fill.Shader = shader;
                    DrawRect(canvas, fill, g.X, g.Y, g.W, g.H, g.Radius);
                    fill.Shader = null; // reset for the next FillRect
                    break;
                }

                case ShadowRect sh:
                {
                    // CSS blur-radius ≈ 2σ; a mask-filter blur softens the shadow edge.
                    var sigma = sh.Blur * 0.5f;
                    using var shPaint = new SKPaint
                    {
                        Color = sh.Color, IsAntialias = true,
                        MaskFilter = sigma > 0.01f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null,
                    };
                    if (!sh.Inset)
                    {
                        // Outset: the box grown by spread, offset, drawn behind the element's background.
                        var rr = new SKRect(sh.X + sh.Dx - sh.Spread, sh.Y + sh.Dy - sh.Spread,
                                            sh.X + sh.W + sh.Dx + sh.Spread, sh.Y + sh.H + sh.Dy + sh.Spread);
                        var rad = MathF.Max(0, sh.Radius + sh.Spread);
                        canvas.DrawRoundRect(rr, rad, rad, shPaint);
                    }
                    else
                    {
                        // Inset: clip to the box, then fill (outer ∖ inner) with even-odd so the blurred
                        // inner edge falls inside the box (inner = the box offset by Dx/Dy, inset by spread).
                        canvas.Save();
                        canvas.ClipRoundRect(new SKRoundRect(new SKRect(sh.X, sh.Y, sh.X + sh.W, sh.Y + sh.H), sh.Radius), antialias: true);
                        var pad = sh.Blur * 2f + MathF.Abs(sh.Spread) + MathF.Max(MathF.Abs(sh.Dx), MathF.Abs(sh.Dy)) + 24f;
                        using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                        path.AddRect(new SKRect(sh.X - pad, sh.Y - pad, sh.X + sh.W + pad, sh.Y + sh.H + pad));
                        var inner = new SKRect(sh.X + sh.Dx + sh.Spread, sh.Y + sh.Dy + sh.Spread,
                                              sh.X + sh.W + sh.Dx - sh.Spread, sh.Y + sh.H + sh.Dy - sh.Spread);
                        path.AddRoundRect(new SKRoundRect(inner, MathF.Max(0, sh.Radius - sh.Spread)));
                        canvas.DrawPath(path, shPaint);
                        canvas.Restore();
                    }
                    break;
                }

                case BorderRect b:
                    stroke.Color = b.Color;
                    DrawBorder(canvas, stroke, b);
                    break;

                case TextRun t:
                    DrawText(canvas, textPaint, t);
                    break;

                case PushClip c:
                    canvas.Save();
                    canvas.ClipRoundRect(new SKRoundRect(new SKRect(c.X, c.Y, c.X + c.W, c.Y + c.H), c.Radius), antialias: true);
                    break;

                case PopClip:
                    canvas.Restore();
                    break;

                case PushTransform t:
                {
                    canvas.Save();
                    var m = SKMatrix.CreateTranslation(t.CenterX + t.TranslateX, t.CenterY + t.TranslateY);
                    m = m.PreConcat(SKMatrix.CreateRotationDegrees(t.RotateDeg));
                    m = m.PreConcat(SKMatrix.CreateScale(t.ScaleX, t.ScaleY));
                    m = m.PreConcat(SKMatrix.CreateTranslation(-t.CenterX, -t.CenterY));
                    canvas.Concat(in m);
                    break;
                }

                case PopTransform:
                    canvas.Restore();
                    break;

                case PushOpacity o:
                    // SaveLayer with an alpha-only paint composites the whole subtree as a group.
                    using (var layer = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(o.Alpha * 255f)) })
                        SaveLayer(canvas, layer, o.X, o.Y, o.W, o.H);
                    break;

                case PopOpacity:
                    canvas.Restore();
                    break;

                case PushFilter f:
                    // SaveLayer with an image filter applies the CSS filter chain to the whole subtree
                    // when the layer is composited on the matching PopFilter/Restore. Bounding the layer
                    // to the element's box (not the whole canvas) is what keeps filters cheap.
                    using (var imf = BuildFilter(f.Ops))
                    using (var layer = new SKPaint { ImageFilter = imf })
                        SaveLayer(canvas, layer, f.X, f.Y, f.W, f.H);
                    break;

                case PopFilter:
                    canvas.Restore();
                    break;

                case Polyline pl when pl.Points.Count >= 4:
                {
                    using var path = new SKPath();
                    path.MoveTo(pl.Points[0], pl.Points[1]);
                    AppendChartPath(path, pl.Points, pl.Curved);

                    if (pl.Fill.Alpha > 0) // area under the line, closed down to the baseline
                    {
                        using var area = new SKPath();
                        area.MoveTo(pl.Points[0], pl.BaseY);
                        area.LineTo(pl.Points[0], pl.Points[1]);
                        AppendChartPath(area, pl.Points, pl.Curved);
                        area.LineTo(pl.Points[^2], pl.BaseY);
                        area.Close();
                        fill.Color = pl.Fill;
                        canvas.DrawPath(area, fill);
                    }
                    if (pl.Width > 0)
                    {
                        stroke.Style = SKPaintStyle.Stroke;
                        stroke.StrokeWidth = pl.Width;
                        stroke.StrokeJoin = SKStrokeJoin.Round;
                        stroke.StrokeCap = SKStrokeCap.Round;
                        stroke.Color = pl.Stroke;
                        canvas.DrawPath(path, stroke);
                        stroke.Style = SKPaintStyle.Fill; // reset for the next border/grip
                    }
                    break;
                }

                case ResizeGrip g:
                {
                    stroke.Style = SKPaintStyle.Stroke;
                    stroke.StrokeWidth = 1.5f;
                    stroke.StrokeCap = SKStrokeCap.Round;
                    stroke.Color = g.Color;
                    float gx2 = g.X + g.Size, gy2 = g.Y + g.Size; // three ticks parallel to the corner diagonal
                    foreach (var f in stackalloc[] { 0.30f, 0.62f, 0.94f })
                        canvas.DrawLine(gx2 - g.Size * f, gy2, gx2, gy2 - g.Size * f, stroke);
                    stroke.Style = SKPaintStyle.Fill;
                    stroke.StrokeCap = SKStrokeCap.Butt;
                    break;
                }

                case DrawImage di:
                {
                    canvas.Save();
                    var box = new SKRect(di.X, di.Y, di.X + di.W, di.Y + di.H);
                    if (di.Radius > 0) canvas.ClipRoundRect(new SKRoundRect(box, di.Radius), antialias: true);
                    else canvas.ClipRect(box);
                    canvas.DrawImage(di.Image, FitRect(di), _imageSampling);
                    canvas.Restore();
                    break;
                }

                case DrawSurface ds:
                {
                    // Resolve NOW: the producer may have swapped frames since the list was built
                    // (that's the point — the list needn't rebuild per frame). A torn-down source
                    // (null frame) simply paints nothing; the element's background shows.
                    if (ds.Source.CurrentFrame is not { } liveFrame) break;
                    canvas.Save();
                    var sbox = new SKRect(ds.X, ds.Y, ds.X + ds.W, ds.Y + ds.H);
                    if (ds.Radius > 0) canvas.ClipRoundRect(new SKRoundRect(sbox, ds.Radius), antialias: true);
                    else canvas.ClipRect(sbox);
                    canvas.DrawImage(liveFrame, FitRect(new DrawImage(ds.X, ds.Y, ds.W, ds.H, liveFrame, ds.Fit, ds.Radius)), _imageSampling);
                    canvas.Restore();
                    break;
                }

                case ClearHole ch:
                {
                    // BlendMode.Src REPLACES what's below with transparent pixels — an underlaid
                    // host element (the web video) shows through; later commands paint on top.
                    using var punch = new SKPaint { Color = SKColors.Transparent, BlendMode = SKBlendMode.Src, IsAntialias = true };
                    var hole = new SKRect(ch.X, ch.Y, ch.X + ch.W, ch.Y + ch.H);
                    if (ch.Radius > 0) canvas.DrawRoundRect(new SKRoundRect(hole, ch.Radius), punch);
                    else canvas.DrawRect(hole, punch);
                    break;
                }

                case FillPath p:
                    using (var path = SKPath.ParseSvgPathData(p.PathData))
                    {
                        if (path is null || p.ViewBox <= 0) break;
                        fill.Color = p.Color;
                        canvas.Save();
                        canvas.Translate(p.X, p.Y);
                        canvas.Scale(p.Width / p.ViewBox, p.Height / p.ViewBox);
                        canvas.DrawPath(path, fill);
                        canvas.Restore();
                    }
                    break;
            }
        }
    }

    private static void DrawRect(SKCanvas canvas, SKPaint paint, float x, float y, float w, float h, float radius)
    {
        var rect = new SKRect(x, y, x + w, y + h);
        if (radius > 0) canvas.DrawRoundRect(rect, radius, radius, paint);
        else canvas.DrawRect(rect, paint);
    }

    // Build one SKImageFilter from a CSS filter chain: colour-matrix ops (brightness…invert) fold into
    // a single colour filter; blur and drop-shadow are image filters composed in declaration order.
    private static SKImageFilter? BuildFilter(IReadOnlyList<FilterOp> ops)
    {
        SKImageFilter? img = null;
        SKColorFilter? color = null;
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case FilterKind.Blur:
                    if (op.A > 0) img = SKImageFilter.CreateBlur(op.A, op.A, img); // CSS radius ≈ Gaussian σ
                    break;
                case FilterKind.DropShadow:
                    img = SKImageFilter.CreateDropShadow(op.A, op.B, MathF.Max(0, op.C), MathF.Max(0, op.C), op.Color, img);
                    break;
                default:
                    var m = ColorMatrix(op);
                    if (m is not null)
                    {
                        var cf = SKColorFilter.CreateColorMatrix(m);
                        color = color is null ? cf : SKColorFilter.CreateCompose(cf, color);
                    }
                    break;
            }
        }
        if (color is not null) img = SKImageFilter.CreateColorFilter(color, img); // apply colour after blur/shadow
        return img;
    }

    // A 4×5 row-major colour matrix for one colour-adjust filter (offset column in 0..255).
    private static float[]? ColorMatrix(FilterOp op)
    {
        float a = op.A;
        switch (op.Kind)
        {
            case FilterKind.Brightness:
                return [a, 0, 0, 0, 0,  0, a, 0, 0, 0,  0, 0, a, 0, 0,  0, 0, 0, 1, 0];
            case FilterKind.Contrast:
            {
                // Offset column is normalised [0,1] (Skia scales RGBA to [0,1] before the matrix).
                float t = 0.5f - 0.5f * a;
                return [a, 0, 0, 0, t,  0, a, 0, 0, t,  0, 0, a, 0, t,  0, 0, 0, 1, 0];
            }
            case FilterKind.Invert:
            {
                float s = 1f - 2f * a, o = a; // offset in [0,1]
                return [s, 0, 0, 0, o,  0, s, 0, 0, o,  0, 0, s, 0, o,  0, 0, 0, 1, 0];
            }
            case FilterKind.Opacity:
                return [1, 0, 0, 0, 0,  0, 1, 0, 0, 0,  0, 0, 1, 0, 0,  0, 0, 0, a, 0];
            case FilterKind.Grayscale:
            {
                // Lerp identity → luminance by amount a.
                float g = Math.Clamp(a, 0f, 1f);
                float rr = 0.2126f, gg = 0.7152f, bb = 0.0722f;
                return
                [
                    1 - g + g * rr, g * gg, g * bb, 0, 0,
                    g * rr, 1 - g + g * gg, g * bb, 0, 0,
                    g * rr, g * gg, 1 - g + g * bb, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            case FilterKind.Sepia:
            {
                float s = Math.Clamp(a, 0f, 1f);
                return
                [
                    1 - s + s * 0.393f, s * 0.769f, s * 0.189f, 0, 0,
                    s * 0.349f, 1 - s + s * 0.686f, s * 0.168f, 0, 0,
                    s * 0.272f, s * 0.534f, 1 - s + s * 0.131f, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            case FilterKind.Saturate:
            {
                // Standard saturation matrix (a=1 → identity, 0 → greyscale, >1 → oversaturated).
                float rw = 0.3086f, gw = 0.6094f, bw = 0.0820f, inv = 1f - a;
                return
                [
                    inv * rw + a, inv * gw, inv * bw, 0, 0,
                    inv * rw, inv * gw + a, inv * bw, 0, 0,
                    inv * rw, inv * gw, inv * bw + a, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            }
            default: return null;
        }
    }

    private static void DrawBorder(SKCanvas canvas, SKPaint paint, BorderRect b)
    {
        var uniform = b.Top > 0 && Approximately(b.Top, b.Right) && Approximately(b.Top, b.Bottom) && Approximately(b.Top, b.Left);
        if (uniform)
        {
            // Stroke centred on the border-box edge, inset by half width.
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = b.Top;
            var inset = b.Top / 2f;
            var rect = new SKRect(b.X + inset, b.Y + inset, b.X + b.W - inset, b.Y + b.H - inset);
            var r = MathF.Max(0, b.Radius - inset);

            // dashed/dotted → a dash path effect on the stroke (dotted = round-capped zero-length dashes).
            SKPathEffect? dash = null;
            if (b.Style == BorderLineStyle.Dashed) { paint.StrokeCap = SKStrokeCap.Butt; dash = SKPathEffect.CreateDash([b.Top * 2.5f, b.Top * 2.5f], 0f); }
            else if (b.Style == BorderLineStyle.Dotted) { paint.StrokeCap = SKStrokeCap.Round; dash = SKPathEffect.CreateDash([0.1f, b.Top * 2f], 0f); }
            paint.PathEffect = dash;

            if (r > 0) canvas.DrawRoundRect(rect, r, r, paint);
            else canvas.DrawRect(rect, paint);

            paint.PathEffect = null; dash?.Dispose();
            paint.StrokeCap = SKStrokeCap.Butt;
            paint.Style = SKPaintStyle.Fill;
            return;
        }

        // Non-uniform: draw each present edge as a filled rectangle.
        if (b.Top > 0) canvas.DrawRect(new SKRect(b.X, b.Y, b.X + b.W, b.Y + b.Top), paint);
        if (b.Bottom > 0) canvas.DrawRect(new SKRect(b.X, b.Y + b.H - b.Bottom, b.X + b.W, b.Y + b.H), paint);
        if (b.Left > 0) canvas.DrawRect(new SKRect(b.X, b.Y, b.X + b.Left, b.Y + b.H), paint);
        if (b.Right > 0) canvas.DrawRect(new SKRect(b.X + b.W - b.Right, b.Y, b.X + b.W, b.Y + b.H), paint);
    }

    private void DrawText(SKCanvas canvas, SKPaint paint, TextRun t)
    {
        var font = _fonts.GetFont(t.Family, t.Weight, t.Size, t.Slant);
        paint.Color = t.Color;

        var alignOffset = t.Align switch
        {
            TextAlign.Center => (t.ContainerWidth - t.LineWidth) / 2f,
            TextAlign.Right => t.ContainerWidth - t.LineWidth,
            _ => 0f,
        };

        // Baseline: centre the *cap height* within the line box (reads as centred, since
        // descender space isn't counted). Falls back to ~0.7em when the font omits cap height.
        var metrics = font.Metrics;
        var cap = metrics.CapHeight > 0 ? metrics.CapHeight : t.Size * 0.7f;
        var x = t.X + MathF.Max(0, alignOffset);
        var baseline = t.Y + t.LineHeight / 2f + cap / 2f;

        // Reorder into visual runs (bidi), then shape each run in its own direction.
        var runs = Bidi.Reorder(t.Text);
        if (runs.Count == 1 && !runs[0].Rtl)
        {
            DrawRun(canvas, paint, font, t.Family, t.Weight, t.Slant, t.Text, x, baseline);
        }
        else
        {
            var cursor = x;
            foreach (var run in runs)
            {
                DrawRun(canvas, paint, font, t.Family, t.Weight, t.Slant, run.Text, cursor, baseline);
                cursor += _fonts.MeasureText(t.Family, t.Weight, t.Size, run.Text, t.Slant);
            }
        }

        if (t.Decorations != TextDecorations.None) DrawDecorations(canvas, paint, t, font, metrics, x, baseline);
    }

    // Underline / line-through / overline across the run, in the text colour. Uses the face's own
    // metrics where it publishes them (position + thickness vary a lot between faces) and falls back
    // to conventional fractions of the em otherwise.
    private static void DrawDecorations(SKCanvas canvas, SKPaint paint, TextRun t, SKFont font,
        SKFontMetrics metrics, float x, float baseline)
    {
        var thickness = metrics.UnderlineThickness is > 0 ? metrics.UnderlineThickness!.Value : MathF.Max(1f, t.Size / 14f);
        var x2 = x + t.LineWidth;
        var wasStroke = paint.Style;
        paint.Style = SKPaintStyle.Fill;

        void Line(float y) => canvas.DrawRect(SKRect.Create(x, y - thickness / 2f, x2 - x, thickness), paint);

        if (t.Decorations.HasFlag(TextDecorations.Underline))
            Line(baseline + (metrics.UnderlinePosition is > 0 ? metrics.UnderlinePosition!.Value : t.Size * 0.12f));
        if (t.Decorations.HasFlag(TextDecorations.LineThrough))
        {
            // Strike through the middle of the x-height, not the baseline.
            var xh = metrics.XHeight > 0 ? metrics.XHeight : t.Size * 0.5f;
            Line(baseline - xh / 2f);
        }
        if (t.Decorations.HasFlag(TextDecorations.Overline))
            Line(baseline - (metrics.CapHeight > 0 ? metrics.CapHeight : t.Size * 0.7f) - thickness);
        paint.Style = wasStroke;
    }

    private void DrawRun(SKCanvas canvas, SKPaint paint, SKFont primaryFont, string family, int weight, FontSlant slant, string text, float x, float baseline)
    {
        // Split into fallback-face runs so glyphs the primary lacks (emoji/CJK/symbols) draw in a
        // face that has them instead of tofu. Each sub-run is HarfBuzz-shaped in its own typeface.
        // (The overwhelmingly common case is one run in the primary face — then we shape once, no
        // extra measuring.)
        var runs = _fonts.SplitRuns(text, family, weight, slant);
        for (var i = 0; i < runs.Count; i++)
        {
            var (segment, tf) = runs[i];
            var font = _fonts.GetFont(tf, primaryFont.Size);
            var shaper = _fonts.GetShaper(tf);
            try { canvas.DrawShapedText(shaper, segment, x, baseline, font, paint); }
            catch { canvas.DrawText(segment, x, baseline, font, paint); }
            if (i < runs.Count - 1) // advance to the next run only when one follows
            {
                try { x += shaper.Shape(segment, font).Width; }
                catch { x += font.MeasureText(segment); }
            }
        }
    }

    private static bool Approximately(float a, float b) => MathF.Abs(a - b) < 0.01f;
}
