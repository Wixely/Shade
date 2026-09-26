using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Shade;

// NativeAOT produces one native executable but cannot fold in unmanaged dependencies: Skia,
// HarfBuzz, GLFW and SDL are shared libraries that have to exist as files before they can be
// loaded. The experimental single-file AOT build embeds them, and this restores them before
// anything asks for them, so one executable is all that ships. Every other build embeds nothing
// and this does nothing.
//
// The restore directory is per user under local application data, never a shared temporary
// directory: putting a world-writable directory on the library search path would invite another
// account to plant a library and have Shade load it. The directory is named after the content it
// holds, and every file is verified by SHA-256 on each start, so a tampered, truncated or
// half-written library is replaced rather than loaded.
[SupportedOSPlatform("windows")]
internal static class EmbeddedNativeLibraries
{
    private const string Prefix = "Shade.Native.";

    internal static void Restore()
    {
        var assembly = typeof(EmbeddedNativeLibraries).Assembly;
        var names = Array.FindAll(assembly.GetManifestResourceNames(),
            name => name.StartsWith(Prefix, StringComparison.Ordinal));
        if (names.Length == 0) return;
        Array.Sort(names, StringComparer.Ordinal);

        var payloads = new List<(string File, byte[] Bytes, byte[] Hash)>(names.Length);
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in names)
        {
            var file = name[Prefix.Length..];
            // The names come from this repository's own build, but a library is about to be loaded
            // from the result, so nothing that could escape the directory is accepted.
            if (file.Length == 0 || file.Contains('/', StringComparison.Ordinal) ||
                file.Contains('\\', StringComparison.Ordinal) || file != Path.GetFileName(file))
                throw new InvalidOperationException("Embedded native library names must be plain file names.");
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("An embedded native library could not be read.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var hash = SHA256.HashData(bytes);
            payloads.Add((file, bytes, hash));
            identity.AppendData(hash);
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shade", "native", Convert.ToHexStringLower(identity.GetCurrentHash())[..16]);
        Directory.CreateDirectory(directory);
        foreach (var (file, bytes, hash) in payloads) Write(Path.Combine(directory, file), bytes, hash);
        // Adds one directory to the standard search order, and drops the current directory from it,
        // without disabling the ordinary system search the way SetDefaultDllDirectories would.
        if (!SetDllDirectory(directory))
            throw new InvalidOperationException("The embedded native libraries could not be made loadable.");
    }

    private static void Write(string path, byte[] bytes, byte[] hash)
    {
        // Reuse only a byte-identical file, so a partial write from an interrupted start, or an
        // altered library, is replaced instead of loaded.
        if (File.Exists(path))
        {
            try
            {
                if (SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(hash)) return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        // Write beside the target and move into place, so another start never sees a partial file.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) when (File.Exists(path) && Matches(path, hash))
        {
            // Another Shade restored the same verified content first.
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static bool Matches(string path, byte[] hash)
    {
        try { return SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(hash); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);
}
