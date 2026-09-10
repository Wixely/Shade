namespace Shade;

// Display IDs are session-local. Persistence must use a separately verified identity strategy.
public sealed record Display(string Id, string Label, int X, int Y, int Width, int Height);

public static class DimLevel
{
    public const int Maximum = 80;
    public static int Validate(int value) => value is >= 0 and <= Maximum
        ? value : throw new ArgumentOutOfRangeException(nameof(value), "Dimming must be between 0 and 80 percent.");
    public static byte Alpha(int value) => (byte)Math.Round(Validate(value) * 255d / 100);
}

public interface IDimmingBackend : IDisposable
{
    IReadOnlyList<Display> Displays { get; }
    string Status { get; }
    int GetLevel(string id);
    void SetLevel(string id, int level);
    void RestoreAll();
}
