using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shade;

public sealed record BrokerConnection(string Host, int Port, bool Tls, string Username, string Password)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Contains('/') || Host.Any(char.IsWhiteSpace)
            || Port is < 1 or > 65535 || Username.Length > 512 || Password.Length > 4096)
            throw new ArgumentException("Enter a broker hostname and port from 1 to 65535.");
    }
}
public sealed class AutomationSettings
{
    public int Version { get; set; } = 1;
    public string InstallationId { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 8883;
    public bool Tls { get; set; } = true;
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public HashSet<string> PublishedScreens { get; set; } = [];
}

public sealed class AutomationSettingsStore(string? path)
{
    public string? Error { get; private set; }
    private bool preserve;
    public bool PreservingUnreadableFile => preserve;
    public AutomationSettings Load()
    {
        if (path is null || !File.Exists(path)) return new();
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
            var value = JsonSerializer.Deserialize(File.ReadAllText(path), ShadeJsonContext.Default.AutomationSettings) ?? throw new InvalidDataException();
            if (value.Version != 1 || !Guid.TryParseExact(value.InstallationId, "N", out _) || value.PublishedScreens is null
                || value.Host is null || value.Username is null || value.ProtectedPassword is null
                || value.Port is < 1 or > 65535 || value.PublishedScreens.Any(id => !ValidTopicId(id))) throw new InvalidDataException();
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { preserve = true; Error = "Integration settings could not be loaded. Integration stays off; file preserved."; return new(); }
    }
    internal static bool ValidTopicId(string? id) => id is { Length: > 0 and < 256 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public bool Recover(AutomationSettings settings)
    {
        if (!preserve || path is null) return Save(settings);
        var backup = path + ".backup-" + Guid.NewGuid().ToString("N");
        var moved = false;
        try
        {
            if (File.Exists(path)) { File.Move(path, backup); moved = true; }
            preserve = false;
            if (Save(settings)) return true;
            preserve = true;
            if (moved && !File.Exists(path)) File.Move(backup, path);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            preserve = true;
            Error = "Integration settings could not be backed up and saved. Check folder access and retry.";
            return false;
        }
    }
    public bool Save(AutomationSettings settings)
    {
        if (preserve) return false;
        if (path is null) return true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            { JsonSerializer.Serialize(stream, settings, ShadeJsonContext.Default.AutomationSettings); stream.Flush(true); }
            File.Move(temporary, path, true); Error = null; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "Integration settings could not be saved. Check access to the settings folder."; return false; }
    }
    public static string Protect(string password)
    {
        if (password.Length == 0) return "";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Remembering passwords requires a supported system credential store.");
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
    }
    public static Task<string> ProtectAsync(string password) => password.Length != 0 && OperatingSystem.IsLinux()
        ? LinuxCredentialProtection.ProtectAsync(password) : Task.FromResult(Protect(password));
    public static Task<string> UnprotectAsync(string value) => value.Length != 0 && OperatingSystem.IsLinux()
        ? LinuxCredentialProtection.UnprotectAsync(value) : Task.FromResult(Unprotect(value));
    public static string Unprotect(string value)
    {
        if (value.Length == 0) return "";
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
    }
}
