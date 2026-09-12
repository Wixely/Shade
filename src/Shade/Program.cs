using CupriFace.Shell;
using Shade;

if (args.Length >= 2 && args[0] == "--wayland-worker" && OperatingSystem.IsLinux())
    return await WaylandOverlayWorker.Run(string.IsNullOrEmpty(args[1]) ? null : args[1]);

if (args.Length >= 2 && args[0] == "--x11-worker" && OperatingSystem.IsLinux())
    return X11OverlayWorker.Run(string.IsNullOrEmpty(args[1]) ? null : args[1], args.Contains("--test-key"));

if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Shade requires Windows or Linux.");
    return 2;
}

try
{
    var session = OperatingSystem.IsWindows() ? "Local\\Shade.Desktop" : LinuxDesktopSession.Current().MutexName;
    using var singleInstance = SingleInstanceGuard.TryAcquire(session);
    if (singleInstance is null) { Console.Error.WriteLine("Shade is already running in this session."); return 3; }
    var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shade");
    var directoryOption = Array.IndexOf(args, "--settings-directory");
    if (directoryOption >= 0)
    {
        if (args.Contains("--ephemeral") || directoryOption + 1 >= args.Length ||
            args[directoryOption + 1].StartsWith("--", StringComparison.Ordinal) ||
            args.Count(value => value == "--settings-directory") != 1)
            throw new ArgumentException("Specify one settings directory, without --ephemeral.");
        settingsDirectory = Path.GetFullPath(args[directoryOption + 1]);
    }
    using var integration = new HomeAssistantIntegration(args.Contains("--ephemeral") ? null : Path.Combine(settingsDirectory, "automation.json"));
    // Reverse disposal order removes overlays before waiting for MQTT shutdown.
    var settingsPath = args.Contains("--ephemeral") ? null : Path.Combine(settingsDirectory, "settings.json");
    using IDimmingBackend backend = OperatingSystem.IsWindows() ? new WindowsDimmingBackend(settingsPath)
        : OperatingSystem.IsLinux() ? new LinuxDimmingBackend(settingsPath) : throw new PlatformNotSupportedException();
    _ = integration.ResumeAsync();
    using var bundledLibraries = new BundledUiLibraries();
    // Register the packaged GLFW window/input implementations explicitly. The software host
    // uses CupriFace's direct SDL implementation and does not need Silk platform discovery.
    Silk.NET.Windowing.Window.ShouldLoadFirstPartyPlatforms(false);
    Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
    Silk.NET.Input.InputWindowExtensions.ShouldLoadFirstPartyPlatforms(false);
    Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();
    DesktopHost.Run(new ShadeApp(backend, integration, closeToTray: !args.Contains("--no-tray")));
    return 0;
}
catch (Exception ex)
{
    // Avoid dumping native paths, monitor identifiers or environment details into logs.
    Console.Error.WriteLine($"Shade could not run ({ex.GetType().Name}). All owned overlays have been closed.");
    // Explicit local diagnostics can contain paths and dependency details; default output stays concise.
    if (args.Contains("--diagnostics")) Console.Error.WriteLine(ex);
    return 1;
}
