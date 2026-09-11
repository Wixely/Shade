using CupriFace.Style;
using SkiaSharp;

namespace CupriFace.Paint;

/// <summary>
/// An immutable list of absolute-positioned paint commands — the commit-snapshot
/// boundary from DESIGN.md §7.2. The UI thread *builds* a DisplayList from the laid-out
/// render tree; a render thread *rasterises* it, never touching live tree state. For M0
/// the two run on one thread, but the seam is the immutable snapshot below.
/// </summary>
public sealed class DisplayList
{
    private readonly List<PaintCommand> _commands = new();
    public IReadOnlyList<PaintCommand> Commands => _commands;
    internal void Add(PaintCommand cmd) => _commands.Add(cmd);
    public int Count => _commands.Count;
}

public abstract record PaintCommand;

public sealed record FillRect(float X, float Y, float W, float H, float Radius, SKColor Color) : PaintCommand;

/// <summary>A CSS <c>box-shadow</c> layer for the (rounded) box (X,Y,W,H,Radius): an outset drop shadow
/// behind the box, or an <paramref name="Inset"/> inner shadow clipped inside it. Offset by (Dx,Dy),
/// softened by <paramref name="Blur"/>, grown/shrunk by <paramref name="Spread"/>.</summary>
public sealed record ShadowRect(float X, float Y, float W, float H, float Radius,
    float Dx, float Dy, float Blur, float Spread, SKColor Color, bool Inset) : PaintCommand;

/// <summary>Fill the (rounded) box (X,Y,W,H,Radius) with a CSS gradient (linear/radial).</summary>
public sealed record GradientRect(float X, float Y, float W, float H, float Radius, Gradient Gradient) : PaintCommand;

public sealed record BorderRect(
    float X, float Y, float W, float H, float Radius,
    float Top, float Right, float Bottom, float Left, SKColor Color,
    BorderLineStyle Style = BorderLineStyle.Solid) : PaintCommand;

public sealed record TextRun(
    float X, float Y, float ContainerWidth, float LineWidth, float LineHeight,
    string Text, string Family, int Weight, float Size, SKColor Color, TextAlign Align,
    FontSlant Slant = FontSlant.Normal, TextDecorations Decorations = TextDecorations.None) : PaintCommand;

public sealed record PushClip(float X, float Y, float W, float H, float Radius) : PaintCommand;

public sealed record PopClip : PaintCommand;

/// <summary>Push a 2D transform applied around (CenterX, CenterY).</summary>
public sealed record PushTransform(
    float CenterX, float CenterY, float TranslateX, float TranslateY,
    float ScaleX, float ScaleY, float RotateDeg) : PaintCommand;

public sealed record PopTransform : PaintCommand;

/// <summary>Composite the wrapped subtree as a group at <paramref name="Alpha"/> (0..1). Bounds
/// (X,Y,W,H) size the offscreen layer — the element's box; W ≤ 0 means "use the whole clip".</summary>
public sealed record PushOpacity(float Alpha, float X, float Y, float W, float H) : PaintCommand;

public sealed record PopOpacity : PaintCommand;

/// <summary>Composite the wrapped subtree through a CSS <c>filter</c> chain (blur / colour-matrix /
/// drop-shadow). The rasteriser builds the Skia filter from these ops at paint time. Bounds (X,Y,W,H)
/// size the offscreen layer — the element's box grown by the filter's spread; W ≤ 0 means "whole clip"
/// (a full-viewport backdrop). Bounding this is critical: an unbounded SaveLayer allocates a
/// whole-canvas offscreen per filter, which is very slow on the CPU rasteriser.</summary>
public sealed record PushFilter(IReadOnlyList<FilterOp> Ops, float X, float Y, float W, float H) : PaintCommand;

public sealed record PopFilter : PaintCommand;

/// <summary>Fill an SVG path (authored in a <paramref name="ViewBox"/>-square) scaled into the box.</summary>
public sealed record FillPath(
    float X, float Y, float Width, float Height, float ViewBox, string PathData, SKColor Color) : PaintCommand;

/// <summary>A chart line: an optional filled area (down to <paramref name="BaseY"/>) under a stroked
/// polyline. <paramref name="Points"/> is absolute [x0,y0,x1,y1,…]. Width 0 skips the stroke; a
/// transparent <paramref name="Fill"/> skips the area. <paramref name="Curved"/> smooths the path
/// (Catmull-Rom spline) instead of straight segments. Used by the line/sparkline/rolling charts.</summary>
public sealed record Polyline(
    IReadOnlyList<float> Points, float Width, SKColor Stroke, SKColor Fill, float BaseY, bool Curved = false) : PaintCommand;

/// <summary>How an image is fitted into its box (CSS <c>object-fit</c>).</summary>
public enum ObjectFit { Contain, Cover, Fill, None }

/// <summary>A resize grip (corner grab handle) in a control's bottom-right corner (CSS <c>resize</c>).</summary>
public sealed record ResizeGrip(float X, float Y, float Size, SKColor Color) : PaintCommand;

/// <summary>Draw a decoded raster image into the box, fitted per <paramref name="Fit"/>, clipped to
/// the (optionally rounded) box.</summary>
public sealed record DrawImage(
    float X, float Y, float W, float H, SKImage Image, ObjectFit Fit, float Radius) : PaintCommand;

/// <summary>Punch a transparent hole (alpha 0, overriding everything painted below) — for a
/// host-composited surface (the web host's underlaid <c>&lt;video&gt;</c> shows through it, while
/// engine content painted AFTER this still composites on top). The host must present with
/// per-pixel alpha for the hole to matter.</summary>
public sealed record ClearHole(float X, float Y, float W, float H, float Radius) : PaintCommand;

/// <summary>Draw a LIVE surface's current frame (video, future 3D viewports), resolved from the
/// source AT RASTER TIME — not captured at build time like <see cref="DrawImage"/>. That's the
/// surface fast path's foundation: a new video frame changes NOTHING in the display list (the
/// source reference and geometry are identical), so the engine can re-raster the retained list
/// clipped to this box instead of re-deriving the whole page. The record compares by source
/// REFERENCE (players persist across rebuilds), keeping the damage diff exact.</summary>
public sealed record DrawSurface(
    float X, float Y, float W, float H, ISurfaceSource Source, ObjectFit Fit, float Radius) : PaintCommand;
