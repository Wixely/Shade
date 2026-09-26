using CupriFace.Shell;
using Shade;

// The Linux backends start overlay workers as child processes with a fixed argument shape. They are
// recognized before ordinary parsing so worker arguments never reach the user-facing command line.
if (args.Length >= 2 && args[0] == "--wayland-worker" && OperatingSystem.IsLinux())
    return await WaylandOverlayWorker.Run(string.IsNullOrEmpty(args[1]) ? null : args[1]);

if (args.Length >= 2 && args[0] == "--x11-worker" && OperatingSystem.IsLinux())
    return X11OverlayWorker.Run(string.IsNullOrEmpty(args[1]) ? null : args[1], args.Contains("--test-key"));

// Before anything writes: a windows-subsystem application has no console of its own, so a command
// line run from a terminal has to join that terminal's console to be seen at all.
if (OperatingSystem.IsWindows()) ConsoleOutput.UseParentConsole();

ShadeInvocation invocation;
try { invocation = ShadeCommandLine.Parse(args); }
catch (CommandLineException ex) { Console.Error.WriteLine(ex.Message); return 4; }

if (invocation.Help) { Console.Out.Write(ShadeCommandLine.Help); return 0; }
if (invocation.Version) { Console.Out.WriteLine(ShadeCommandLine.Version); return 0; }

if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("Shade requires Windows or Linux.");
    return 2;
}

try
{
    var session = OperatingSystem.IsWindows() ? "Local\\Shade.Desktop" : LinuxDesktopSession.Current().MutexName;
    // Windows pipe names are machine-wide, so the channel name separates accounts and
    // terminal-services sessions explicitly. On Linux the session name already does.
    var channel = InstanceChannel.NameFor(OperatingSystem.IsWindows()
        ? $"windows:{Environment.UserDomainName}\\{Environment.UserName}:{System.Diagnostics.Process.GetCurrentProcess().SessionId}"
        : "linux:" + session);
    using var singleInstance = SingleInstanceGuard.TryAcquire(session);
    if (singleInstance is null)
    {
        // A duplicate launch never opens a second window. It hands its command line to the running
        // instance, which applies it immediately and comes forward.
        if (invocation.ChangesStartup)
        {
            Console.Error.WriteLine("Shade is already running in this session. Startup options cannot be changed in a running instance.");
            return 5;
        }
        try
        {
            var reply = await InstanceChannel.SendAsync(channel, invocation.Forwarded(), process =>
            {
                // Windows only lets the process the user just launched pass the foreground right on.
                if (OperatingSystem.IsWindows()) WindowActivation.AllowForeground(process);
            });
            foreach (var line in reply.Lines) Console.Out.WriteLine(line);
            if (reply.Ok) return 0;
            Console.Error.WriteLine(reply.Message);
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Shade is already running in this session, but it did not answer on its control channel.");
            if (invocation.Diagnostics) Console.Error.WriteLine(ex);
            return 3;
        }
    }
    // Holding the lock proves nothing else is running, so there is no state to report and nothing
    // to quit. Neither request starts Shade.
    if (invocation.RequiresRunningInstance) { Console.Out.WriteLine("Shade is not running."); return 0; }

    var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shade");
    if (invocation.SettingsDirectory is { } chosen) settingsDirectory = chosen;
    using var integration = new HomeAssistantIntegration(invocation.Ephemeral ? null : Path.Combine(settingsDirectory, "automation.json"));
    // Reverse disposal order removes overlays before waiting for MQTT shutdown.
    var settingsPath = invocation.Ephemeral ? null : Path.Combine(settingsDirectory, "settings.json");
    using IDimmingBackend backend = OperatingSystem.IsWindows() ? new WindowsDimmingBackend(settingsPath)
        : OperatingSystem.IsLinux() ? new LinuxDimmingBackend(settingsPath) : throw new PlatformNotSupportedException();
    _ = integration.ResumeAsync();
    using var bundledLibraries = new BundledUiLibraries();

    var requests = new InstanceRequests();
    // Control options given at startup are applied through the same queue as forwarded ones, on the
    // first interface tick, so one code path owns every command-line change.
    requests.Enqueue(invocation.AtStartup());
    using var server = InstanceServer.TryStart(channel, requests);
    if (server is null)
        Console.Error.WriteLine("Shade could not open its control channel; command lines cannot be forwarded to this instance.");
    var guard = singleInstance;
    void Shutdown()
    {
        // The window loop cannot return on request, so overlays and workers are removed here, on the
        // interface thread that owns them, before the process stops.
        try { backend.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { integration.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { guard.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        Environment.Exit(0);
    }

    // A single-file NativeAOT build carries its unmanaged dependencies as resources; restore them
    // before anything loads Skia, GLFW or SDL. A no-op in every other build.
    if (OperatingSystem.IsWindows()) EmbeddedNativeLibraries.Restore();

    // Register the packaged GLFW window/input implementations explicitly. The software host
    // uses CupriFace's direct SDL implementation and does not need Silk platform discovery.
    Silk.NET.Windowing.Window.ShouldLoadFirstPartyPlatforms(false);
    Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
    Silk.NET.Input.InputWindowExtensions.ShouldLoadFirstPartyPlatforms(false);
    Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();
    DesktopHost.Run(new ShadeApp(backend, integration, closeToTray: !invocation.NoTray, requests, Shutdown));
    return 0;
}
catch (Exception ex)
{
    // Avoid dumping native paths, monitor identifiers or environment details into logs.
    Console.Error.WriteLine($"Shade could not run ({ex.GetType().Name}). All owned overlays have been closed.");
    // Explicit local diagnostics can contain paths and dependency details; default output stays concise.
    if (invocation.Diagnostics) Console.Error.WriteLine(ex);
    return 1;
}
