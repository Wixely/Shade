using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace Shade;

// Silk's OS-name loader does not search .NET's single-file native extraction directories.
// Resolve only the exact bundled UI libraries from runtime-provided directories before host selection.
internal sealed class BundledUiLibraries : IDisposable
{
    private readonly List<nint> libraries = [];
    private DefaultPathResolver? resolver;
    private Func<string, IEnumerable<string>>? resolveBundled;
    public BundledUiLibraries()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string paths) return;
        if (OperatingSystem.IsLinux())
        {
            // SDL's requested filename differs from its ELF SONAME, so preloading alone
            // cannot satisfy Silk's later dlopen. Supply exact bundled paths instead.
            var directories = paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (PathResolver.Default is DefaultPathResolver defaultResolver)
            {
                resolver = defaultResolver;
                resolveBundled = name =>
                {
                    // Silk 2.22 requests the older GLFW filename; Ultz 3.4 ships .so.3.
                    var bundled = name switch
                    {
                        "libglfw.so.3.3" or "libglfw.so.3" => "libglfw.so.3",
                        "libSDL2-2.0.so" => "libSDL2-2.0.so",
                        _ => null
                    };
                    return bundled is null ? [] : directories.Select(directory => Path.Combine(directory, bundled)).Where(File.Exists);
                };
                resolver.Resolvers.Insert(0, resolveBundled);
            }
            return;
        }
        try
        {
            foreach (var name in new[] { "glfw3.dll", "SDL2.dll" })
            {
                foreach (var directory in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = Path.Combine(directory, name);
                    if (!File.Exists(path)) continue;
                    if (NativeLibrary.TryLoad(path, out var library))
                    {
                        libraries.Add(library);
                        break;
                    }
                }
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (resolver is not null && resolveBundled is not null) resolver.Resolvers.Remove(resolveBundled);
        resolver = null;
        resolveBundled = null;
        for (var i = libraries.Count - 1; i >= 0; i--) NativeLibrary.Free(libraries[i]);
        libraries.Clear();
    }
}
