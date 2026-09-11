namespace CupriFace.Text;

/// <summary>An <c>@font-face</c> rule or font file that could not be loaded: the family (or file
/// name) it was for, the sources tried, and the last error.</summary>
public sealed record FontFaceProblem(string Family, IReadOnlyList<string> Sources, string Reason);

/// <summary>
/// What a document's text actually resolved to. <see cref="Resolutions"/> lists every family the
/// cascade asked for and what answered it; under <see cref="FontPolicy.Platform"/> the entries whose
/// <see cref="FontResolution.Source"/> is not <see cref="FontSource.Registered"/> are the ones another
/// machine may render differently. <see cref="Problems"/> is what failed to load.
/// </summary>
public sealed record FontReport(
    IReadOnlyList<FontResolution> Resolutions,
    IReadOnlyList<string> RegisteredFamilies,
    IReadOnlyList<FontFaceProblem> Problems)
{
    /// <summary>True when every family resolved to a registered face and nothing failed to load —
    /// the text lays out the same on every platform, and renders the same on every machine of one
    /// platform (Skia's glyph rasteriser differs between Windows, Linux and macOS).</summary>
    public bool IsDeterministic => Problems.Count == 0 && Resolutions.All(r => r.Source == FontSource.Registered);

    public override string ToString()
    {
        var lines = new List<string>();
        foreach (var r in Resolutions)
            lines.Add($"{r.Family} {r.Weight}{(r.Slant == Style.FontSlant.Normal ? "" : " " + r.Slant.ToString().ToLowerInvariant())} -> {r.ResolvedFamily} ({r.Source})");
        foreach (var p in Problems)
            lines.Add($"! {p.Family}: {p.Reason} [{string.Join(", ", p.Sources)}]");
        return string.Join(Environment.NewLine, lines);
    }
}
