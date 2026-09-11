using System.Globalization;

namespace Shade;

public sealed record MonitorTile(Display Display, int Number, double X, double Y, double Width, double Height)
{
    public string Style => FormattableString.Invariant($"left:{X:0.##}px;top:{Y:0.##}px;width:{Width:0.##}px;height:{Height:0.##}px;");
}

public sealed record MonitorLayout(IReadOnlyList<MonitorTile> Tiles, double Width, double Height)
{
    public string CanvasStyle => $"width:{Width.ToString("0.##", CultureInfo.InvariantCulture)}px;height:{Height.ToString("0.##", CultureInfo.InvariantCulture)}px;";

    public static MonitorLayout Create(IReadOnlyList<Display> displays)
    {
        if (displays.Count == 0) return new([], 600, 120);
        var left = displays.Min(d => (double)d.X);
        var top = displays.Min(d => (double)d.Y);
        var width = displays.Max(d => (double)d.X + d.Width) - left;
        var height = displays.Max(d => (double)d.Y + d.Height) - top;
        // Preserve one common scale, including portrait screens, negative coordinates and gaps.
        // Very small screens remain usable by allowing the map to scroll instead of distorting them.
        var scale = Math.Max(Math.Min(592 / width, 260 / height),
            Math.Max(76d / displays.Min(d => Math.Max(1, d.Width)), 60d / displays.Min(d => Math.Max(1, d.Height))));
        var tiles = displays.Select((d, i) => new MonitorTile(d, i + 1,
            (d.X - left) * scale + 4, (d.Y - top) * scale + 4,
            Math.Max(1, d.Width * scale - 8), Math.Max(1, d.Height * scale - 8))).ToArray();
        return new(tiles, width * scale, height * scale);
    }
}
