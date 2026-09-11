namespace CupriFace.Text;

/// <summary>A directional run of text in visual (left-to-right) order.</summary>
public readonly record struct BidiRun(string Text, bool Rtl);

/// <summary>
/// Simplified Unicode Bidirectional Algorithm (base direction LTR). Segments a line into
/// directional runs and reorders them for visual display: contiguous RTL runs are
/// reversed as a group; HarfBuzz then shapes each run in its own direction. This is not
/// the full UBA (no explicit embeddings / weak-type resolution), but handles the common
/// case of LTR text with embedded Arabic/Hebrew runs.
/// </summary>
public static class Bidi
{
    private static bool IsRtl(char c) =>
        (c >= 0x0590 && c <= 0x05FF) ||   // Hebrew
        (c >= 0x0600 && c <= 0x06FF) ||   // Arabic
        (c >= 0x0750 && c <= 0x077F) ||   // Arabic Supplement
        (c >= 0x08A0 && c <= 0x08FF) ||   // Arabic Extended-A
        (c >= 0xFB50 && c <= 0xFDFF) ||   // Arabic Presentation Forms-A
        (c >= 0xFE70 && c <= 0xFEFF);     // Arabic Presentation Forms-B

    private static bool IsStrongLtr(char c) => char.IsLetterOrDigit(c) && !IsRtl(c);

    /// <summary>Reorder a line into visual-order runs. Fast-paths pure-LTR text.</summary>
    public static List<BidiRun> Reorder(string line)
    {
        var hasRtl = false;
        foreach (var c in line) if (IsRtl(c)) { hasRtl = true; break; }
        if (!hasRtl) return new List<BidiRun> { new(line, false) };

        // Per-char level: RTL → 1; neutrals inherit the last strong level (base 0).
        var levels = new int[line.Length];
        var lastStrong = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (IsRtl(c)) { levels[i] = 1; lastStrong = 1; }
            else if (IsStrongLtr(c)) { levels[i] = 0; lastStrong = 0; }
            else levels[i] = lastStrong; // neutral
        }

        // Group contiguous equal-level chars into logical runs.
        var runs = new List<(string Text, int Level)>();
        var start = 0;
        for (var i = 1; i <= line.Length; i++)
        {
            if (i == line.Length || levels[i] != levels[start])
            {
                runs.Add((line[start..i], levels[start]));
                start = i;
            }
        }

        // UBA rule L2 (two levels only): reverse contiguous level-1 run groups.
        for (var i = 0; i < runs.Count;)
        {
            if (runs[i].Level == 1)
            {
                var j = i;
                while (j < runs.Count && runs[j].Level == 1) j++;
                runs.Reverse(i, j - i);
                i = j;
            }
            else i++;
        }

        var result = new List<BidiRun>(runs.Count);
        foreach (var (text, level) in runs) result.Add(new BidiRun(text, level == 1));
        return result;
    }
}
