using CupriFace.Shell;
using Shade;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This feasibility prototype implements Windows shading only. Linux support remains planned.");
    return 2;
}

try
{
    using var backend = new WindowsDimmingBackend();
    DesktopHost.Run(new ShadeApp(backend));
    return 0;
}
catch (Exception ex)
{
    // Avoid dumping native paths, monitor identifiers or environment details into logs.
    Console.Error.WriteLine($"Shade could not run ({ex.GetType().Name}). All owned overlays have been closed.");
    return 1;
}
