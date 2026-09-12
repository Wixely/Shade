namespace Shade;

public sealed record Display(string Id, string Label, int X, int Y, int Width, int Height,
    bool CanRemember = false, string IdentityNote = "Session identity; settings not remembered");

public static class DimLevel
{
    public const int DefaultMaximum = 80;
    public const int Maximum = 100;
    public static int Validate(int value) => value is >= 0 and <= Maximum
        ? value : throw new ArgumentOutOfRangeException(nameof(value), "Dimming must be between 0 and 100 percent.");
    public static byte Alpha(int value) => (byte)Math.Round(Validate(value) * 255d / 100);
}

public interface IDimmingBackend : IDisposable
{
    IReadOnlyList<Display> Displays { get; }
    string Status { get; }
    long Revision { get; }
    long RestoreVersion { get; }
    ControlPreferences? Preferences => null;
    void SavePreferences(ControlPreferences preferences) { }
    int GetLevel(string id);
    void SetLevel(string id, int level);
    void SetLevels(IReadOnlyDictionary<string, int> levels)
    {
        foreach (var level in levels) SetLevel(level.Key, level.Value);
    }
    void RestoreAll();
    bool SupportsAssignments => false;
    IReadOnlyList<AssignmentChoice> Assignments => Array.Empty<AssignmentChoice>();
    void AssignScreen(string connectionId, string name, string? existingId = null) => throw new NotSupportedException();
    void DetachAssignment(string id) => throw new NotSupportedException();
    bool SettingsNeedRecovery => false;
    bool SettingsNeedBackup => false;
    void RecoverSettings() => throw new NotSupportedException();
    bool RecoverySetupNeeded => false;
    void ConfigureRecovery() => throw new NotSupportedException();
}
