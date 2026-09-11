using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using CupriFace.Binding;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;

namespace CupriFace;

/// <summary>
/// Agent / developer introspection (DESIGN.md §12 debug channel). <see cref="DebugDump"/>
/// lays out the document and emits a single JSON snapshot an AI agent (or a human) can read
/// to diagnose a live form without a screen: the render tree with layout boxes and key
/// styles, the current interaction state (focus/caret/hover/scroll/overlays), the two-way
/// bound values, and the accessibility (semantics) tree. Read-only — it never mutates state.
/// </summary>
public sealed partial class CupriDocument
{
    /// <summary>
    /// Lay out at the given size and return an indented JSON diagnostic snapshot. Safe to call
    /// any time; does not change document or interaction state.
    /// </summary>
    public string DebugDump(float width, float height)
    {
        _layout.Layout(_root, width, height);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();

            w.WriteStartObject("viewport");
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            w.WriteEndObject();

            WriteInteraction(w);
            WriteBindings(w);

            w.WritePropertyName("tree");
            WriteNode(w, _root);

            w.WritePropertyName("a11y");
            WriteA11y(w, Accessibility.AccessibilityTree.Build(_root));

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteInteraction(Utf8JsonWriter w)
    {
        w.WriteStartObject("interaction");

        w.WritePropertyName("focus");
        if (_focusKey is null) w.WriteNullValue();
        else
        {
            w.WriteStartObject();
            w.WriteString("key", _focusKey);
            w.WriteNumber("caret", _caret);
            if (_editBuffer is not null) w.WriteString("buffer", _editBuffer);
            if (_focusNumeric)
            {
                w.WriteBoolean("numeric", true);
                w.WriteBoolean("bufferValid", _editBuffer is null || BufferValid(_editBuffer));
                if (_focusMin is { } mn) w.WriteNumber("min", mn);
                if (_focusMax is { } mx) w.WriteNumber("max", mx);
            }
            w.WriteEndObject();
        }

        w.WriteBoolean("dragging", _dragging);
        if (_dragging && _dragPath is not null) w.WriteString("dragPath", _dragPath);

        w.WriteStartArray("hover");
        foreach (var el in _hoverChain) w.WriteStringValue(Describe(el));
        w.WriteEndArray();

        // Overlays: every element with a two-way-bound `open`, and its current state.
        w.WriteStartObject("overlays");
        var seen = new HashSet<string>();
        foreach (var n in Descendants(_root))
            if (n.Element?.GetAttribute("data-bind-open") is { Length: > 0 } path && seen.Add(path))
                w.WriteBoolean(path, BindingEngine.Resolve(_model, path) as bool? ?? false);
        w.WriteEndObject();

        w.WriteEndObject();
    }

    // The two-way bound model state: every data-bind-* path in the tree, resolved to its value.
    private void WriteBindings(Utf8JsonWriter w)
    {
        w.WriteStartObject("bindings");
        var seen = new HashSet<string>();
        foreach (var n in Descendants(_root))
        {
            if (n.Element is null) continue;
            foreach (var attr in n.Element.Attributes)
            {
                if (!attr.Name.StartsWith("data-bind-", StringComparison.Ordinal)) continue;
                var path = attr.Value;
                if (path.Length == 0 || !seen.Add(path)) continue;
                w.WritePropertyName(path);
                WriteValue(w, BindingEngine.Resolve(_model, path));
            }
        }
        w.WriteEndObject();
    }

    private void WriteNode(Utf8JsonWriter w, RenderNode n)
    {
        w.WriteStartObject();

        if (n.IsText)
        {
            w.WriteString("text", n.Text);
        }
        else
        {
            w.WriteString("tag", n.Tag);
            if (n.Element?.GetAttribute("id") is { Length: > 0 } id) w.WriteString("id", id);
            if (n.Element?.GetAttribute("class") is { Length: > 0 } cls) w.WriteString("class", cls);
            if (n.Element?.GetAttribute("role") is { Length: > 0 } role) w.WriteString("role", role);
        }

        var (x, y, bw, bh) = HitTesting.AbsoluteBox(n);
        w.WriteStartArray("box");
        foreach (var v in new[] { x, y, bw, bh }) w.WriteNumberValue(MathF.Round(v, 1));
        w.WriteEndArray();

        // A compact set of the styles that most often explain a visual bug.
        if (!n.IsText)
        {
            w.WriteString("display", n.Style.Display.ToString());
            if (n.Style.Background.Alpha != 0) w.WriteString("bg", Hex(n.Style.Background));
        }
        w.WriteString("color", Hex(n.Style.Color));
        w.WriteNumber("fontSize", n.Style.FontSize);

        // State flags that survive/drive interaction.
        var flags = new List<string>();
        if (n.Element?.HasAttribute("data-focus") == true) flags.Add("focus");
        if (n.Element?.HasAttribute("data-hover") == true) flags.Add("hover");
        if (n.Element?.HasAttribute("data-active") == true) flags.Add("active");
        if (n.Element?.HasAttribute("data-invalid") == true) flags.Add("invalid");
        if (n.IsTopLayer) flags.Add("top-layer");
        if (n.IsScrollable) flags.Add("scrollable");
        if (flags.Count > 0)
        {
            w.WriteStartArray("flags");
            foreach (var f in flags) w.WriteStringValue(f);
            w.WriteEndArray();
        }
        if (n.IsScrollable)
        {
            w.WriteStartObject("scroll");
            w.WriteNumber("y", MathF.Round(n.ScrollY, 1));
            w.WriteNumber("max", MathF.Round(n.MaxScrollY, 1));
            w.WriteEndObject();
        }

        var kids = n.Children.Where(c => c.Style.Display != DisplayType.None).ToList();
        if (kids.Count > 0)
        {
            w.WriteStartArray("children");
            foreach (var c in kids) WriteNode(w, c);
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteA11y(Utf8JsonWriter w, Accessibility.AccessibilityNode n)
    {
        w.WriteStartObject();
        w.WriteString("role", n.Role);
        if (n.Name is { Length: > 0 }) w.WriteString("name", n.Name);
        if (n.Value is { Length: > 0 }) w.WriteString("value", n.Value);
        if (n.Focusable) w.WriteBoolean("focusable", true);
        if (n.Disabled) w.WriteBoolean("disabled", true);
        if (n.Checked is { } chk) w.WriteBoolean("checked", chk);
        if (n.Now is { } now) w.WriteNumber("now", now);
        if (n.Children.Count > 0)
        {
            w.WriteStartArray("children");
            foreach (var c in n.Children) WriteA11y(w, c);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    // ---- helpers -------------------------------------------------------------
    private static IEnumerable<RenderNode> Descendants(RenderNode root)
    {
        yield return root;
        foreach (var c in root.Children)
            foreach (var d in Descendants(c))
                yield return d;
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case float f: w.WriteNumberValue(f); break;
            default: w.WriteStringValue(value.ToString()); break;
        }
    }

    private static string Describe(IElement el)
    {
        var sb = new StringBuilder(el.LocalName);
        if (el.GetAttribute("id") is { Length: > 0 } id) sb.Append('#').Append(id);
        if (el.ClassList.Length > 0) sb.Append('.').Append(el.ClassList[0]);
        return sb.ToString();
    }

    private static string Hex(SkiaSharp.SKColor c) =>
        c.Alpha == 255 ? $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}"
                       : $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}{c.Alpha:X2}";
}
