using AngleSharp.Dom;
using CupriFace.Style;

namespace CupriFace.Dom;

/// <summary>
/// A node in the render tree (mirrors the relevant DOM subtree). Carries computed
/// style and, after layout, its border-box geometry relative to the parent's content
/// box. Text nodes have <see cref="Text"/> set and no <see cref="Element"/>.
/// </summary>
public sealed class RenderNode
{
    public string Tag = "";
    public IElement? Element;
    public string? Text;              // non-null for text nodes
    public ComputedStyle Style = new();
    public RenderNode? Parent;
    public readonly List<RenderNode> Children = new();

    // ---- Layout results (border-box, in parent content coordinates) ----
    public float X, Y, Width, Height;

    // Resolved box metrics (px)
    public float MarginTop, MarginRight, MarginBottom, MarginLeft;
    public float PadTop, PadRight, PadBottom, PadLeft;
    public float BorderTopW, BorderRightW, BorderBottomW, BorderLeftW;

    // For text nodes: laid-out lines (set by the text layout pass in M2).
    public List<TextLine>? Lines;

    // Cache of the wrapped text layout: LayoutText reuses Lines when the text, width, and font are all
    // unchanged (every frame of an animation that isn't resizing this text), skipping the re-split /
    // re-measure / TextLine allocations. Invalidated automatically — a rebuild makes a fresh node.
    public TextLayoutKey? TextKey;
    public float TextW, TextH;

    // For an inline element with a background/border (e.g. a <code> chip): one rounded box per line it
    // spans, in the block's content coordinates. Set by the inline formatting context; painted behind
    // the element's text. Null for a plain passthrough inline element.
    public List<InlineRect>? InlineFragments;

    // Collapsed whitespace at this node's edges in the source (incl. whitespace between inline siblings).
    // Used by the inline formatting context to keep spaces between flowed runs (e.g. "text <code>x</code>").
    public bool WsBefore, WsAfter;

    // For icon nodes: an SVG path (24×24 viewBox) filled with the computed color.
    public string? IconPath;

    // For image nodes (<cupri-image>): the source to decode + paint (resolved by ImageStore).
    public string? ImageSrc;

    // For live-surface nodes (<cupri-video>, future 3D viewports): the SurfaceRegistry key whose
    // current frame paints here. When no frame exists yet, ImageSrc (the poster) paints instead.
    public string? SurfaceKey;

    // For chart plots (line chart / sparkline): normalised points "x0,y0 x1,y1 …" in 0..1 (y=0 top),
    // painted as a polyline (+ area fill / dots) scaled into the content box. Set from data-cupri-line.
    public string? ChartLine;

    // Overlays: position:fixed nodes are lifted to the top layer and painted last,
    // with X/Y already in absolute viewport coordinates.
    public bool IsTopLayer;

    // Scrolling (overflow:scroll/auto): full content extent + current offset. ScrollY is
    // interaction state — layout recomputes ScrollContentHeight but preserves ScrollY.
    public float ScrollContentHeight;
    public float ScrollY;
    public float ScrollX; // horizontal caret scroll for a single-line (white-space:nowrap) text field

    public float ContentBoxWidth => Width - HorizontalInsets;

    // Natural (content-sized) border-box height from the last layout, computed before any explicit
    // height/resize constraint is applied. Lets a height transition animate to/from `height:auto`
    // (the target when auto = this), and it stays correct even while the node is height-constrained.
    public float ContentNaturalHeight;

    // The actual laid-out height shown last frame, carried across the rebuild (like scroll state). A
    // height transition animates FROM this — the truth on screen — so a collapse/expand starts from where
    // the element visibly is, even the first time and even from an initially-expanded state.
    public float PrevHeight;

    // True once LayoutNode has laid this node out. A rebuild makes fresh nodes (false) and doesn't lay
    // them out, so a second rebuild before the next layout (a hover-update right after a click) must NOT
    // read this node's 0 height as its displayed height — CaptureScroll carries PrevHeight forward instead.
    public bool LaidOut;

    // User-dragged size (CSS resize) — interaction state, preserved across rebuilds like ScrollY.
    // Null = use the CSS size. Overrides width/height in layout, then clamped to min/max-*.
    public float? ResizeW, ResizeH;

    // Split pane (interaction state): a flex-grow override set by dragging a divider, so a panel's share
    // of the split follows the divider. Null = use the CSS flex-grow. Preserved across rebuilds like resize.
    public float? SplitGrow;

    // Drag-to-reorder (interaction state, paint-time): the current Y offset applied to this item, the
    // offset it's easing toward (0, or ±one slot to open the gap), and whether it is the lifted item being
    // dragged (which tracks the pointer directly — DragOffsetX/Y, no easing — and paints on top with a shadow).
    // DragOffsetX is only set on the lifted card, so it can follow the pointer across kanban columns.
    public float DragOffsetY, DragTargetY, DragOffsetX;
    public bool Dragging;
    public float ContentBoxHeight => Height - VerticalInsets;
    /// <summary>How far the content is currently pulled PAST its edge by a finger that kept
    /// dragging — the rubber band. Transient interaction state: applied while the finger is down,
    /// sprung back to zero on release. Positive pulls content up (dragged past the bottom).</summary>
    public float OverscrollY;

    /// <summary>Where the content actually sits: the clamped scroll offset plus any rubber band.
    /// Paint and hit-testing both read this, so what you see and what you can touch agree even
    /// mid-stretch.</summary>
    public float EffectiveScrollY => Math.Clamp(ScrollY, 0, MaxScrollY) + OverscrollY;

    public float MaxScrollY => MathF.Max(0, ScrollContentHeight - ContentBoxHeight);
    public bool IsScrollable => MaxScrollY > 0.5f;

    /// <summary>The widest child extent inside a scroll container, mirroring
    /// <see cref="ScrollContentHeight"/>. Zero for anything that is not an overflow:scroll box —
    /// including single-line text fields, whose <see cref="ScrollX"/> is a caret-follow shift they
    /// manage themselves and which must keep working exactly as it did.</summary>
    public float ScrollContentWidth;
    public float MaxScrollX => MathF.Max(0, ScrollContentWidth - ContentBoxWidth);

    /// <summary>Deliberately SEPARATE from <see cref="IsScrollable"/>, which means "overflows
    /// vertically" and is load-bearing for paint culling, wheel routing and scroll capture. A box
    /// can overflow on one axis, the other, or both.</summary>
    public bool IsScrollableX => MaxScrollX > 0.5f;

    /// <summary>The horizontal offset actually applied, clamped to the content — the analogue of
    /// the clamping the vertical path does at paint time.</summary>
    public float ClampedScrollX => IsScrollableX ? Math.Clamp(ScrollX, 0, MaxScrollX) : ScrollX;

    /// <summary><see cref="OverscrollY"/>'s sideways twin. Positive pulls content left (dragged
    /// past the right-hand end).</summary>
    public float OverscrollX;

    /// <summary>Where the content actually sits horizontally. Guarded on
    /// <see cref="IsScrollableX"/> because <see cref="ScrollX"/> doubles as a single-line text
    /// field's caret-follow shift — a field is not a scroller and must never acquire a band.</summary>
    public float EffectiveScrollX => IsScrollableX ? ClampedScrollX + OverscrollX : ScrollX;

    public bool IsText => Text is not null;

    public float ContentLeftInset => BorderLeftW + PadLeft;
    public float ContentTopInset => BorderTopW + PadTop;
    public float HorizontalInsets => BorderLeftW + PadLeft + PadRight + BorderRightW;
    public float VerticalInsets => BorderTopW + PadTop + PadBottom + BorderBottomW;

    public void AddChild(RenderNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }
}

/// <summary>A single laid-out line of text produced by the text layout pass.</summary>
public sealed class TextLine
{
    public required string Text;
    public float X, Y;      // baseline-independent top-left, relative to node content box
    public float Width, Height;
}

/// <summary>One background/border box of an inline element, in the block's content coordinates.</summary>
public readonly record struct InlineRect(float X, float Y, float W, float H);

/// <summary>The inputs that determine a text node's wrapped layout — its cache key (see RenderNode.Lines).</summary>
public readonly record struct TextLayoutKey(string Text, float MaxW, float Size, int Weight, string Family, float LineH, Style.WhiteSpaceMode Wrap, Style.FontSlant Slant, bool BreakAll, bool BreakWord);
