using System.Text;
using CupriFace.Style;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CupriFace.Text;

/// <summary>Where a family's typeface came from. Under <see cref="FontPolicy.RegisteredOnly"/> only
/// the first ever occurs.</summary>
public enum FontSource
{
    /// <summary>A face the app registered — the same bytes on every machine.</summary>
    Registered,
    /// <summary>The platform's font for that family name — whatever this machine has installed.</summary>
    Platform,
    /// <summary>Skia's default face, because the platform had nothing by that name.</summary>
    Default,
}

/// <summary>
/// What a document may resolve a family to. <see cref="Platform"/> is the everyday setting: a
/// registered face wins, and anything else falls to what the machine has, which is what an app on a
/// desktop expects. <see cref="RegisteredOnly"/> is the setting for output that must not depend on
/// the machine — a test image, a rendered frame: a family with no registered face is an error naming
/// the family, and glyph fallback for missing characters searches the registered faces rather than
/// the platform's. Nothing about the machine's FONTS can reach the output; the layout is then the
/// same on every platform, and the pixels the same on every machine of one platform (Skia's glyph
/// rasteriser differs between Windows, Linux and macOS, so pixels differ across those).
/// </summary>
public enum FontPolicy { Platform, RegisteredOnly }

/// <summary>One family the document asked for, and what answered.</summary>
public sealed record FontResolution(string Family, int Weight, FontSlant Slant, string ResolvedFamily, FontSource Source);

/// <summary>Thrown under <see cref="FontPolicy.RegisteredOnly"/> for a family no registered face
/// covers. The message names the family, because "the text looks slightly different on the build
/// server" is the alternative.</summary>
public sealed class FontNotRegisteredException(string family)
    : InvalidOperationException($"No registered font covers the family \"{family}\", and the font policy is RegisteredOnly. Register a face for it (an @font-face rule, LoadFont, or CupriApp.Fonts), or use FontPolicy.Platform.")
{
    public string Family { get; } = family;
}

/// <summary>
/// Caches typefaces and fonts by (family, weight, size). DESIGN.md §7.5 — text is
/// shaped/measured once and reused; recreating <see cref="SKFont"/> per frame would
/// allocate and re-rasterise. Not thread-safe (single UI thread for now).
///
/// <b>Fallback-face selection</b> (DESIGN risk #5): a run whose characters the primary family lacks
/// (emoji, CJK, symbols) is split off and rendered in a fallback face found via
/// <see cref="SKFontManager.MatchCharacter(string, SKFontStyle, string[], int)"/> — otherwise those
/// glyphs render as tofu. See <see cref="SplitRuns"/>.
/// </summary>
public sealed class FontService : IDisposable
{
    // Every cache keys on the SLANT too: italic is a different face with different metrics, so sharing
    // an entry with the upright face would measure and draw the wrong thing.
    private readonly Dictionary<(string, int, FontSlant), SKTypeface> _typefaces = new();
    private readonly Dictionary<(string, int, int, FontSlant), SKFont> _fonts = new();
    private readonly Dictionary<(string, int, FontSlant), SKShaper> _shapers = new();
    private readonly Dictionary<(SKTypeface, int), SKFont> _fontsByTypeface = new();
    private readonly Dictionary<SKTypeface, SKShaper> _shapersByTypeface = new();
    private readonly Dictionary<SKTypeface, SKFont> _probes = new();      // cached font per typeface for glyph checks
    private readonly Dictionary<int, SKTypeface?> _fallbackByCodepoint = new(); // fallback face per missing codepoint
    private readonly Dictionary<(string, int, int, FontSlant, string), float> _measure = new(); // + slant → width
    private readonly Dictionary<(string, int, FontSlant), FontResolution> _resolutions = new();

    internal static SKFontStyleSlant Slant(FontSlant s) => s switch
    {
        FontSlant.Italic => SKFontStyleSlant.Italic,
        FontSlant.Oblique => SKFontStyleSlant.Oblique,
        _ => SKFontStyleSlant.Upright,
    };

    // ---- registered (embedded) fonts ---------------------------------------
    // A host can supply font DATA instead of relying on platform fonts — essential in the browser,
    // where the wasm libSkiaSharp ships exactly ONE embedded face ("Noto Mono"), so without this every
    // family — including sans-serif — silently renders monospaced. Registered faces are consulted
    // before the platform, and the first registered family becomes the target of the generic families
    // ("sans-serif" etc.). "monospace" is left to the platform on purpose (Noto Mono on wasm,
    // Consolas/DejaVu on desktops) — except under RegisteredOnly, where nothing is.
    //
    // Keyed by WEIGHT BUCKET (100–900), not by a bold bit: a design system ships Light, Regular,
    // Medium, SemiBold and Bold, and CSS's nearest-weight rule picks between them. A face declared
    // for a weight RANGE is registered at every bucket in it.
    private readonly Dictionary<(string Family, int Weight, bool Italic), SKTypeface> _registeredFaces = new();
    private readonly HashSet<SKTypeface> _registeredTypefaces = new();  // distinct, for disposal
    private string? _registeredDefault; // family the generic sans aliases resolve to
    private FontPolicy _policy;

    /// <summary>See <see cref="FontPolicy"/>. Changing it re-resolves every family on next use.</summary>
    public FontPolicy Policy
    {
        get => _policy;
        set { if (_policy != value) { _policy = value; InvalidateResolution(); } }
    }

    /// <summary>Register a font from raw TTF/OTF/TTC or WOFF 1 bytes (e.g. an embedded resource).
    /// Family, weight and slant are read from the font itself; register each style you need
    /// (Regular, Bold, …). WOFF 2 is recognised and refused by name — it needs a decoder this engine
    /// does not yet carry.</summary>
    public void RegisterFont(byte[] data) => RegisterFont(data, null, null, null, null);

    /// <summary>Register a font under DECLARED metadata, the way an <c>@font-face</c> rule does: the
    /// family the cascade will match, the weight (or range) and slant it answers for. Nulls fall back
    /// to what the font says about itself.</summary>
    public SKTypeface RegisterFont(byte[] data, string? family, int? weightMin, int? weightMax, FontSlant? slant)
    {
        if (Woff.IsWoff2(data))
            throw new NotSupportedException("WOFF 2 fonts are not supported yet (Brotli plus a glyf/loca transform to undo). Convert to TTF/OTF or WOFF 1.");
        var sfnt = Woff.IsWoff1(data) ? Woff.ToSfnt(data) : data;

        using var skData = SKData.CreateCopy(sfnt);
        var tf = SKTypeface.FromData(skData)
                 ?? throw new ArgumentException("Not a readable font.", nameof(data));

        var fam = (family ?? tf.FamilyName).ToLowerInvariant();
        var italic = slant.HasValue ? slant.Value != FontSlant.Normal : tf.FontStyle.Slant != SKFontStyleSlant.Upright;
        var lo = Bucket(weightMin ?? tf.FontStyle.Weight);
        var hi = Bucket(weightMax ?? weightMin ?? tf.FontStyle.Weight);
        if (hi < lo) (lo, hi) = (hi, lo);
        for (var w = lo; w <= hi; w += 100) _registeredFaces[(fam, w, italic)] = tf;
        _registeredTypefaces.Add(tf);
        _registeredDefault ??= fam;
        InvalidateResolution();
        return tf;
    }

    /// <summary>Every family the document has asked for so far, and what answered. Under
    /// <see cref="FontPolicy.RegisteredOnly"/> every entry is <see cref="FontSource.Registered"/>;
    /// under <see cref="FontPolicy.Platform"/> this is the list of what would differ on another
    /// machine.</summary>
    public IReadOnlyList<FontResolution> Resolutions => _resolutions.Values.ToList();

    /// <summary>The registered families, for diagnostics.</summary>
    public IReadOnlyList<string> RegisteredFamilies => _registeredFaces.Keys.Select(k => k.Family).Distinct().ToList();

    private void InvalidateResolution()
    {
        _typefaces.Clear(); _fonts.Clear(); _shapers.Clear(); _measure.Clear(); _resolutions.Clear();
        _fallbackByCodepoint.Clear();
    }

    private static int Bucket(int weight) => Math.Clamp((int)Math.Round(weight / 100.0) * 100, 100, 900);

    private static bool IsGenericSans(string lowerFamily) =>
        lowerFamily is "sans-serif" or "serif" or "system-ui" or "ui-sans-serif" or "-apple-system";

    private SKTypeface? Registered(string lowerFamily, int weight, FontSlant slant)
    {
        if (_registeredFaces.Count == 0) return null;
        var family = IsGenericSans(lowerFamily) && !HasFamily(lowerFamily) ? _registeredDefault : lowerFamily;
        if (family is null) return null;
        var italic = slant != FontSlant.Normal;
        // The style first: an italic request with no italic face takes the upright one rather than
        // nothing, and vice versa — a wrong slant beats a wrong family.
        return Nearest(family, weight, italic) ?? Nearest(family, weight, !italic);
    }

    private bool HasFamily(string lowerFamily)
    {
        foreach (var k in _registeredFaces.Keys) if (k.Family == lowerFamily) return true;
        return false;
    }

    // CSS font-matching for weight: at or below 400 prefer lighter faces going down, then heavier
    // going up; at or above 500 prefer heavier going up, then lighter going down — except that 400
    // and 500 try each other first, so Regular and Medium stand in for one another before Bold does.
    private SKTypeface? Nearest(string family, int weight, bool italic)
    {
        var want = Bucket(weight);
        if (_registeredFaces.TryGetValue((family, want, italic), out var exact)) return exact;
        if (want is 400 or 500 && _registeredFaces.TryGetValue((family, 900 - want, italic), out var twin)) return twin;
        var down = want <= 400;
        for (var pass = 0; pass < 2; pass++)
        {
            var step = down ? -100 : 100;
            for (var w = want + step; w is >= 100 and <= 900; w += step)
                if (_registeredFaces.TryGetValue((family, w, italic), out var tf)) return tf;
            down = !down;
        }
        return null;
    }

    public SKTypeface GetTypeface(string family, int weight, FontSlant slant = FontSlant.Normal)
    {
        var key = (family.ToLowerInvariant(), weight, slant);
        if (_typefaces.TryGetValue(key, out var tf)) return tf;

        // Registered (embedded) faces win; then the platform. A family with no italic face: ask the
        // platform to synthesise/substitute rather than silently rendering upright.
        FontSource source;
        tf = Registered(key.Item1, weight, slant);
        if (tf is not null) source = FontSource.Registered;
        else if (_policy == FontPolicy.RegisteredOnly) throw new FontNotRegisteredException(family);
        else
        {
            var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, Slant(slant));
            tf = SKTypeface.FromFamilyName(family, style);
            // Skia answers an unknown family with its default face rather than null; say which.
            source = tf is not null && string.Equals(tf.FamilyName, family, StringComparison.OrdinalIgnoreCase)
                ? FontSource.Platform : FontSource.Default;
            tf ??= SKTypeface.Default;
        }
        _typefaces[key] = tf;
        _resolutions[key] = new FontResolution(family, weight, slant, tf.FamilyName, source);
        return tf;
    }

    public SKFont GetFont(ComputedStyle s) => GetFont(s.FontFamily, s.FontWeight, s.FontSize, s.FontStyle);

    public SKFont GetFont(string family, int weight, float size, FontSlant slant = FontSlant.Normal)
    {
        var key = (family.ToLowerInvariant(), weight, (int)MathF.Round(size * 4), slant); // 0.25px buckets
        if (_fonts.TryGetValue(key, out var f)) return f;
        f = new SKFont(GetTypeface(family, weight, slant), size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        _fonts[key] = f;
        return f;
    }

    /// <summary>A HarfBuzz shaper for the (family, weight, slant), cached per typeface.</summary>
    public SKShaper GetShaper(string family, int weight, FontSlant slant = FontSlant.Normal)
    {
        var key = (family.ToLowerInvariant(), weight, slant);
        if (_shapers.TryGetValue(key, out var sh)) return sh;
        sh = new SKShaper(GetTypeface(family, weight, slant));
        _shapers[key] = sh;
        return sh;
    }

    /// <summary>A font for a specific typeface + size (used for fallback runs), cached.</summary>
    public SKFont GetFont(SKTypeface typeface, float size)
    {
        var key = (typeface, (int)MathF.Round(size * 4));
        if (_fontsByTypeface.TryGetValue(key, out var f)) return f;
        f = new SKFont(typeface, size) { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        _fontsByTypeface[key] = f;
        return f;
    }

    /// <summary>A HarfBuzz shaper for a specific typeface (fallback runs), cached.</summary>
    public SKShaper GetShaper(SKTypeface typeface)
    {
        if (_shapersByTypeface.TryGetValue(typeface, out var sh)) return sh;
        sh = new SKShaper(typeface);
        _shapersByTypeface[typeface] = sh;
        return sh;
    }

    /// <summary>Split <paramref name="text"/> into runs by which typeface can render each character:
    /// the primary family, or a fallback face for glyphs it lacks (found once per codepoint and
    /// cached). Runs concatenate back to the original text. Codepoint-aware (surrogate pairs stay
    /// together), so emoji and astral characters route to an emoji/symbol font.</summary>
    public List<(string Text, SKTypeface Typeface)> SplitRuns(string text, string family, int weight,
        FontSlant slant = FontSlant.Normal)
    {
        var primary = GetTypeface(family, weight, slant);
        var runs = new List<(string, SKTypeface)>();
        if (string.IsNullOrEmpty(text)) return runs;

        var sb = new StringBuilder();
        SKTypeface? current = null;
        for (var i = 0; i < text.Length;)
        {
            var high = char.IsHighSurrogate(text[i]) && i + 1 < text.Length;
            var cp = high ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            var len = high ? 2 : 1;
            var tf = TypefaceForCodepoint(primary, family, weight, slant, cp);
            if (current is null) current = tf;
            else if (!ReferenceEquals(tf, current)) { runs.Add((sb.ToString(), current)); sb.Clear(); current = tf; }
            sb.Append(text, i, len);
            i += len;
        }
        if (sb.Length > 0) runs.Add((sb.ToString(), current!));
        return runs;
    }

    // The face to render one codepoint: the primary if it has the glyph (ASCII fast-pathed), else a
    // fallback that does (cached per codepoint); the primary if nothing matches (graceful tofu).
    // Under RegisteredOnly the search is confined to the registered faces: a platform emoji font is
    // exactly the kind of per-machine difference that policy exists to exclude.
    private SKTypeface TypefaceForCodepoint(SKTypeface primary, string family, int weight, FontSlant slant, int cp)
    {
        if (cp < 0x80 || HasGlyph(primary, cp)) return primary;
        if (_fallbackByCodepoint.TryGetValue(cp, out var cached)) return cached ?? primary;
        SKTypeface? fb = null;
        if (_policy == FontPolicy.RegisteredOnly)
        {
            foreach (var tf in _registeredTypefaces)
                if (!ReferenceEquals(tf, primary) && HasGlyph(tf, cp)) { fb = tf; break; }
        }
        else
        {
            try { fb = SKFontManager.Default.MatchCharacter(family, new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, Slant(slant)), null, cp); }
            catch { /* no font manager match → tofu in the primary */ }
        }
        _fallbackByCodepoint[cp] = fb;
        return fb ?? primary;
    }

    private bool HasGlyph(SKTypeface tf, int cp)
    {
        if (!_probes.TryGetValue(tf, out var probe)) { probe = new SKFont(tf, 16f); _probes[tf] = probe; }
        return probe.ContainsGlyph(cp);
    }

    /// <summary>
    /// Measure text width using HarfBuzz shaping (correct advances/kerning/ligatures) across
    /// fallback-face runs, falling back to Skia's simple measurement if the native shaper is
    /// unavailable.
    /// </summary>
    public float MeasureText(string family, int weight, float size, string text, FontSlant slant = FontSlant.Normal)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        // Cache by (font, text): during animation the same words are re-measured every frame, and each
        // miss runs run-splitting + HarfBuzz shaping (the layout pass's dominant cost + allocation).
        // Measurements are deterministic and fonts don't change at runtime, so the cache never invalidates.
        var key = (family, weight, (int)MathF.Round(size * 4), slant, text);
        if (_measure.TryGetValue(key, out var cached)) return cached;

        var total = 0f;
        foreach (var (segment, tf) in SplitRuns(text, family, weight, slant))
        {
            var font = GetFont(tf, size);
            try { total += GetShaper(tf).Shape(segment, font).Width; }
            catch { total += font.MeasureText(segment); }
        }
        _measure[key] = total;
        return total;
    }

    public float MeasureText(ComputedStyle s, string text) => MeasureText(s.FontFamily, s.FontWeight, s.FontSize, text, s.FontStyle);

    /// <summary>Line height in px for a style (font-size × line-height multiple).</summary>
    public static float LineHeightPx(ComputedStyle s) => s.FontSize * s.LineHeight;

    public void Dispose()
    {
        foreach (var sh in _shapers.Values) sh.Dispose();
        foreach (var sh in _shapersByTypeface.Values) sh.Dispose();
        foreach (var f in _fonts.Values) f.Dispose();
        foreach (var f in _fontsByTypeface.Values) f.Dispose();
        foreach (var p in _probes.Values) p.Dispose();
        foreach (var t in _registeredTypefaces) t.Dispose();
        // The resolution cache may hold registered faces — skip those (just disposed above).
        foreach (var t in _typefaces.Values) if (!_registeredTypefaces.Contains(t)) t.Dispose();
        _shapers.Clear(); _shapersByTypeface.Clear();
        _fonts.Clear(); _fontsByTypeface.Clear(); _probes.Clear();
        _typefaces.Clear(); _measure.Clear(); _registeredFaces.Clear(); _registeredTypefaces.Clear(); _resolutions.Clear();
    }
}
