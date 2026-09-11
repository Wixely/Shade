using System.Reflection;

namespace CupriFace.Resources;

/// <summary>
/// THE scheme→bytes resolution for media sources — one definition shared by images
/// (<see cref="Paint.ImageStore"/>) and video (<c>VideoSource</c>), so both carry the same
/// options for developers and the same trust model:
///   <c>data:</c> URI → inline · <c>https://</c> → <see cref="CupriSource.Url"/> under the
///   document's <see cref="CupriSourceOptions"/> policy · <c>file://</c> or a path →
///   <see cref="CupriSource.File"/> · a bare name → <b>embedded</b> in the registered app
///   assembly, falling back to a local file.
/// </summary>
public static class SourceResolver
{
    public static bool IsRemote(string src) =>
        src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bytes for <paramref name="src"/>, or null when unresolved. Remote fetches run
    /// under <paramref name="urlOptions"/> (https-only, size cap, timeout by default) and BLOCK —
    /// callers that must not stall (first paint) do their own backgrounding, exactly like the
    /// image store's async remote path and the video player's deferred open.</summary>
    public static byte[]? Load(string src, Assembly? assembly, CupriSourceOptions? urlOptions)
    {
        try
        {
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma < 0) return null;
                var payload = src[(comma + 1)..];
                return src[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }
            if (IsRemote(src))
                return CupriSource.Url(new Uri(src), urlOptions).ReadBytes();
            if (src.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return CupriSource.File(new Uri(src).LocalPath).ReadBytes();

            // A bare/relative name: embedded in the app assembly if one is registered, else a local file.
            if (assembly is not null)
            {
                try { return CupriSource.Embedded(assembly, src).ReadBytes(); }
                catch (CupriResourceException) { /* not embedded → fall through to a file */ }
            }
            return CupriSource.File(src).ReadBytes();
        }
        catch
        {
            return null; // unresolved/unreadable → the caller shows its fallback (poster, nothing)
        }
    }
}
