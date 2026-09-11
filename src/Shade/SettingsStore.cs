using System.Text.Json;

namespace Shade;

public sealed record RememberedDisplay(int Level, int X, int Y, int Width, int Height);
public sealed record RememberedControl(bool Enabled, bool UseGlobal, int IndividualLevel)
{
    public int EffectiveLevel(int global) => Enabled ? UseGlobal ? global : IndividualLevel : 0;
}
public sealed record ControlPreferences(int GlobalLevel, IReadOnlyDictionary<string, RememberedControl> Screens);
public sealed class ShadeSettings
{
    public int Version { get; set; } = 3;
    public Dictionary<string, ScreenAssignment> Assignments { get; set; } = [];
    public int GlobalLevel { get; set; } = 30;
    public Dictionary<string, RememberedControl> Controls { get; set; } = [];
    public Dictionary<string, RememberedDisplay> Displays { get; set; } = [];
    public HashSet<string> AmbiguousHardware { get; set; } = [];
}

public sealed class SettingsStore(string path)
{
    private bool preserveUnreadableFile;
    public bool PreservingUnreadableFile => preserveUnreadableFile;
    public string? Error { get; private set; }
    public ShadeSettings Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
            var settings = JsonSerializer.Deserialize(File.ReadAllText(path), ShadeJsonContext.Default.ShadeSettings) ?? throw new InvalidDataException();
            if (settings.Version is not (1 or 2 or 3) || settings.Displays is null || settings.AmbiguousHardware is null ||
                settings.Assignments is null || !IdentityAssignments.Valid(settings.Assignments) ||
                settings.Controls is null || settings.GlobalLevel is < 0 or > DimLevel.Maximum ||
                settings.Controls.Any(p => p.Value is null || p.Value.IndividualLevel is < 0 or > DimLevel.Maximum) ||
                settings.Displays.Any(p => p.Value is null || p.Value.Level is < 0 or > DimLevel.Maximum || p.Value.Width <= 0 || p.Value.Height <= 0))
                throw new InvalidDataException();
            if (settings.Version == 1)
            {
                // Preserve old effective levels without guessing that equal levels were linked.
                settings.Controls = settings.Displays.ToDictionary(p => p.Key,
                    p => new RememberedControl(p.Value.Level > 0, false, p.Value.Level > 0 ? p.Value.Level : 30));
            }
            settings.Version = 3;
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            preserveUnreadableFile = true;
            Error = "Saved settings could not be loaded. Started undimmed; existing file preserved.";
            return new();
        }
    }
    public bool Save(ShadeSettings settings)
    {
        // A corrupt/unknown-version file is preserved rather than silently replaced.
        if (preserveUnreadableFile) return false;
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, ShadeJsonContext.Indented.ShadeSettings);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
            Error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "Settings could not be saved. Current shading still works.";
            return false;
        }
    }
    public bool Recover(ShadeSettings settings)
    {
        if (!preserveUnreadableFile) return Save(settings);
        var backup = path + ".backup-" + Guid.NewGuid().ToString("N");
        var moved = false;
        try
        {
            // Same-directory rename preserves the exact original without reading an arbitrarily large file.
            if (File.Exists(path)) { File.Move(path, backup); moved = true; }
            preserveUnreadableFile = false;
            if (Save(settings)) return true;
            preserveUnreadableFile = true;
            if (moved && !File.Exists(path)) File.Move(backup, path);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            preserveUnreadableFile = true;
            Error = "Settings could not be backed up and saved. Check access to the settings folder and retry.";
            return false;
        }
    }
}
