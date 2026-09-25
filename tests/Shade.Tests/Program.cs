using System.Buffers.Binary;
using System.Diagnostics;
using CupriFace.Dom;
using CupriFace.Interaction;
using Shade;
using SkiaSharp;

if (args.Length == 2 && args[0] == "--single-instance-probe")
{
    using var guard = SingleInstanceGuard.TryAcquire(args[1]);
    Console.WriteLine(guard is null ? "duplicate" : "acquired");
    if (guard is null) return 3;
    Console.ReadLine();
    return 0;
}

if (args.Contains("--readme-screenshots"))
{
    ReadmeScreenshots.Capture();
    return 0;
}

if (args.Contains("--accessibility-state-preview"))
{
    CupriFace.Shell.DesktopHost.Run(new AccessibilityStateApp());
    return 0;
}

if (args.Length == 3 && args[0] == "--tls-trust-child" && OperatingSystem.IsLinux())
    return await TlsTrustTests.Child(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args[2] == "success");

if (args.Length >= 2 && args[0] == "--wayland-worker" && OperatingSystem.IsLinux())
    return await WaylandOverlayWorker.Run(string.IsNullOrEmpty(args[1]) ? null : args[1]);

if (args.Length >= 2 && args[0] == "--x11-worker" && OperatingSystem.IsLinux())
    return X11OverlayWorker.Run(string.IsNullOrEmpty(args[1]) ? null : args[1], args.Contains("--test-key"));

if (args.Contains("--preview"))
{
    using var preview = new FakeBackend { RecoverySetupNeeded = args.Contains("--recovery-preview") };
    if (args.Contains("--real-layout") && OperatingSystem.IsWindows())
        preview.ReplaceDisplays(WindowsDisplayCatalog.Read(new HashSet<string>()));
    else
        preview.ReplaceDisplays([
            new("left", "Portrait display", -1080, 0, 1080, 1920),
            new("center", "Main display", 0, 280, 2560, 1440),
            new("right", "Side display", 2560, 280, 1920, 1080)]);
    CupriFace.Shell.DesktopHost.Run(new ShadeApp(preview, closeToTray: args.Contains("--tray")));
    return 0;
}

var failed = 0;
if (args.Contains("--focus-rendering"))
{
    Run("Scrolled focus outline placement and clipping", FocusRenderingTests.Run);
    Run("Keyboard focus scrolls both axes and reverses", FocusRenderingTests.KeyboardScroll);
    Run("Accessible focus reveals controls in both scroll axes", FocusRenderingTests.AccessibilityScroll);
    Run("Accessible activation rejects clipped alternatives", FocusRenderingTests.AccessibilityActivation);
    return failed == 0 ? 0 : 1;
}
if (args.Length >= 3 && args[0] == "--linux-ui-published" && OperatingSystem.IsLinux())
{
    Run("Published Linux control window", () => LinuxUiTests.Run(args[1], args[2], args.Contains("--software"), args.Contains("--interact"), args.Contains("--keyring-unavailable")).GetAwaiter().GetResult());
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--tls-trust-matrix"))
{
    Run("Process-scoped TLS trust, hostname, validity and revocation checks", () => TlsTrustTests.Run().GetAwaiter().GetResult());
    return failed == 0 ? 0 : 1;
}
var linuxPublishedIndex = Array.IndexOf(args, "--linux-published");
if (linuxPublishedIndex >= 0 && (linuxPublishedIndex + 1 >= args.Length || args[linuxPublishedIndex + 1].StartsWith("--") || !args.Contains("--linux-worker")))
    throw new ArgumentException("Use --linux-worker --linux-published <executable> to test the published X11 worker.");
var linuxWorkerExecutable = linuxPublishedIndex >= 0 && linuxPublishedIndex + 1 < args.Length
    ? Path.GetFullPath(args[linuxPublishedIndex + 1]) : null;
if (args.Length == 2 && args[0] == "--wayland-published" && OperatingSystem.IsLinux())
{
    Run("Published Wayland worker with isolated compositor and portal", () => WaylandWorkerTests.Run(Path.GetFullPath(args[1])).GetAwaiter().GetResult());
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--wayland-worker-fixture") && OperatingSystem.IsLinux())
{
    Run("Wayland worker with isolated compositor and portal", () => WaylandWorkerTests.Run().GetAwaiter().GetResult());
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--wayland-worker-unavailable") && OperatingSystem.IsLinux())
{
    Run("Wayland worker selection, unavailable compositor and responsive controls", () =>
    {
        using var backend = new LinuxDimmingBackend(null);
        Assert(backend.Status.Contains("zwlr_layer_shell_v1"), "Native Wayland worker was not selected or limitation was hidden");
        Assert(!backend.RecoverySetupNeeded && backend.Displays.Count == 0, "Unavailable compositor offered shading or prompted for a shortcut");
        var worker = backend.WorkerId;
        backend.SavePreferences(new(41, new Dictionary<string, RememberedControl>()));
        Equal(41, backend.Preferences.GlobalLevel);
        var restored = backend.RestoreVersion; backend.RestoreAll(); Equal(restored + 1, backend.RestoreVersion);
        backend.Refresh(); Equal(41, backend.Preferences.GlobalLevel); Equal(worker, backend.WorkerId);
        Assert(backend.Status.Contains("zwlr_layer_shell_v1"), "Explicit reconnect lost the compositor limitation");
        using var owned = Process.GetProcessById(worker);
        backend.Dispose(); Assert(owned.WaitForExit(5000), "Wayland worker did not exit when its parent closed the pipe");
    });
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--wayland-recovery"))
{
    Run("Wayland recovery through an isolated Global Shortcuts portal", () => WaylandRecoveryTests.Run().GetAwaiter().GetResult());
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--wayland-no-layer"))
{
    Run("Wayland session reports missing layer-shell without mapping surfaces", () =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var session = WaylandOverlaySession.Create(timeout.Token).GetAwaiter().GetResult();
            throw new Exception("This check requires the known compositor without layer-shell");
        }
        catch (NotSupportedException ex) when (ex.Message.Contains("zwlr_layer_shell_v1", StringComparison.Ordinal)) { }
    });
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--wayland-buffers"))
{
    Run("Native Wayland immutable shared-memory palette", () =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = timeout.Token;
        using var wire = WaylandWire.Connect(token).GetAwaiter().GetResult();
        using var catalog = new WaylandOutputCatalog(wire);
        catalog.Initialize(token).GetAwaiter().GetResult();
        var shm = catalog.BindRequired("wl_shm", 1, token).GetAwaiter().GetResult();
        var palette = new WaylandShmPalette(wire, shm);
        palette.Initialize(token).GetAwaiter().GetResult();
        var buffers = Enumerable.Range(1, DimLevel.Maximum).Select(level => palette.GetBuffer(level, token).GetAwaiter().GetResult()).ToArray();
        Assert(buffers.Distinct().Count() == DimLevel.Maximum, "Palette reused a buffer with different pixel contents");
        Equal(buffers[39], palette.GetBuffer(40, token).GetAwaiter().GetResult());
        catalog.Synchronize(token).GetAwaiter().GetResult();
        palette.Destroy(token).GetAwaiter().GetResult();
        catalog.Synchronize(token).GetAwaiter().GetResult();
        Console.WriteLine("Compositor accepted 100 immutable 1x1 ARGB buffers from a 400-byte sealed pool; no surfaces created.");
    });
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--wayland-catalog"))
{
    Run("Native Wayland logical output catalog", () =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var catalog = new WaylandOutputCatalog(WaylandWire.Connect(timeout.Token).GetAwaiter().GetResult());
        catalog.Initialize(timeout.Token).GetAwaiter().GetResult();
        var displays = catalog.Displays;
        Assert(displays.Count > 0 && displays.Select(d => d.Id).Distinct().Count() == displays.Count, "Missing or duplicate Wayland screen identities");
        Assert(displays.All(d => !d.CanRemember && d.Width > 0 && d.Height > 0), "Wayland catalog invented hardware identity or invalid bounds");
        Console.WriteLine($"Wayland logical outputs: {displays.Count}; hardware-trusted identities: 0.");
        foreach (var d in displays) Console.WriteLine($"  {d.X},{d.Y}: {d.Width}x{d.Height}");
        var revision = catalog.Revision;
        using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { while (true) catalog.ReadNext(idle.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (idle.IsCancellationRequested) { }
        Console.WriteLine($"Catalog changes during two-second observation: {catalog.Revision - revision}.");
    });
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--x11-hold-recovery") && OperatingSystem.IsLinux())
{
    X11Native.XInitThreads();
    var nativeError = 0;
    X11Native.ErrorHandler handler = (_, error) => { nativeError = System.Runtime.InteropServices.Marshal.ReadByte(error, 32); return 0; };
    X11Native.XSetErrorHandler(handler);
    var connection = X11Native.XOpenDisplay(0);
    if (connection == 0) return 3;
    try
    {
        var key = X11Native.XKeysymToKeycode(connection, 0xffc8);
        var masks = X11HotkeyModifiers.Read(connection);
        if (key == 0 || masks.Length == 0) return 3;
        foreach (var mask in masks) X11Native.XGrabKey(connection, key, mask, X11Native.XDefaultRootWindow(connection), 0, 1, 1);
        X11Native.XSync(connection, 0);
        if (nativeError != 0) return 4;
        Console.WriteLine("ready"); Console.Out.Flush();
        Console.ReadLine();
    }
    finally { X11Native.XCloseDisplay(connection); GC.KeepAlive(handler); }
    return 0;
}
if (args.Contains("--linux-hotkey") && OperatingSystem.IsLinux()) Run("Linux hotkey contention disables shading and recovers automatically", () =>
{
    var executable = Environment.ProcessPath ?? throw new Exception("No test executable path");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
    start.ArgumentList.Add("--x11-hold-recovery");
    using var blocker = Process.Start(start) ?? throw new Exception("Hotkey helper did not start");
    try
    {
        Equal("ready", blocker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()!);
        using var backend = new LinuxDimmingBackend(null, testKey: true);
        Assert(backend.Displays.Count > 0 && backend.Status.Contains("Recovery hotkey unavailable"), "Contended recovery shortcut did not disable shading");
        Assert(backend.Displays.All(d => OperatingSystem.IsLinux() && backend.GetLevel(d.Id) == 0), "Hotkey contention left shading enabled");
        blocker.StandardInput.Close();
        Assert(blocker.WaitForExit(5000) && blocker.ExitCode == 0, "Hotkey helper did not release its grabs");
        Assert(SpinWait.SpinUntil(() => OperatingSystem.IsLinux() && backend.Status.StartsWith("X11 overlays active"), 5000), "Hotkey did not recover automatically after contention ended");
        backend.VerifyNative();
    }
    finally { if (!blocker.HasExited) { blocker.Kill(); blocker.WaitForExit(); } }
});
if (args.Contains("--wayland-probe"))
{
    Run("Read-only Wayland registry round trip", () => WaylandProbe.Run().GetAwaiter().GetResult());
    return failed == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--ui-broker")
{
    await UiBrokerFixture.Run(args[1]);
    return 0;
}
if (args.Length == 2 && args[0] == "--published-tls")
{
    Run("Published application TLS rejection and native shading isolation", () => TlsTests.Run(args[1]).GetAwaiter().GetResult());
    return failed == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "--published")
{
    Run("Published application MQTT, native shading and restart persistence", () => PublishedApplicationTests.Run(args[1]).GetAwaiter().GetResult());
    return failed == 0 ? 0 : 1;
}
if (args.Contains("--hidden-mqtt") && OperatingSystem.IsWindows()) Run("Live hidden CupriFace MQTT commands and native overlay cleanup", HiddenAutomationTests.Run);
if (OperatingSystem.IsLinux()) Run("Linux credential envelope authenticates ciphertext and preserves Unicode", () =>
{
    var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    var otherKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    try
    {
        const string password = "synthetic-\u00e9-\u65e5\u672c-\U0001f512";
        var first = LinuxCredentialProtection.Encrypt(password, key);
        var second = LinuxCredentialProtection.Encrypt(password, key);
        Assert(first != second, "Repeated encryption reused its nonce");
        Equal(password, LinuxCredentialProtection.Decrypt(first, key));
        Assert(!first.Contains(password), "Envelope exposed plaintext");
        var rejected = false;
        try { LinuxCredentialProtection.Decrypt(first, otherKey); }
        catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
        Assert(rejected, "Wrong key accepted");
        var separator = first.IndexOf(':');
        var bytes = Convert.FromBase64String(first[(separator + 1)..]); bytes[^1] ^= 1;
        rejected = false;
        try { LinuxCredentialProtection.Decrypt(first[..(separator + 1)] + Convert.ToBase64String(bytes), key); }
        catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
        Assert(rejected, "Modified ciphertext accepted");
    }
    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); System.Security.Cryptography.CryptographicOperations.ZeroMemory(otherKey); }
});
if (args.Contains("--linux-no-keyring") && OperatingSystem.IsLinux()) Run("Missing Linux keyring reports failure without saving a password", () =>
{
    if (System.Runtime.InteropServices.NativeLibrary.TryLoad("libsecret-1.so.0", out var library))
    {
        System.Runtime.InteropServices.NativeLibrary.Free(library);
        throw new Exception("This fixture requires missing libsecret; no credential-store operation attempted");
    }
    using var integration = new HomeAssistantIntegration();
    using var backend = new FakeBackend(); backend.SetLevel("a", 15);
    integration.ConfigureAsync(new("127.0.0.1", 1883, false, "synthetic-user", "synthetic-password"), true, false).GetAwaiter().GetResult();
    Assert(integration.Status.Contains("requires libsecret"), "Expected unavailable libsecret status");
    Assert(!integration.Enabled && !integration.HasPassword, "Failed credential write changed saved state");
    Equal(15, backend.GetLevel("a"));
});
if (args.Contains("--tls")) Run("TLS certificate rejection, reconnect and local-control isolation", () => TlsTests.Run().GetAwaiter().GetResult());
if (args.Contains("--mqtt-permissions")) Run("MQTT subscription/publication rejection and permission recovery", () => AutomationPermissionTests.Run().GetAwaiter().GetResult());
if (args.Contains("--linux-focus-guard") && OperatingSystem.IsLinux()) Run("WSLg incompatible compositor stops shading after overlay focus", () =>
{
    using var backend = new LinuxDimmingBackend(null, testKey: true);
    Assert(backend.Displays.Count > 0, backend.Status);
    var id = backend.Displays[0].Id;
    backend.SetLevel(id, 3);
    Assert(SpinWait.SpinUntil(() => OperatingSystem.IsLinux() && backend.Status.Contains("preserve keyboard input") && backend.GetLevel(id) == 0, 3000), "Compositor focus guard did not disable shading");
    backend.Refresh();
    Equal(0, backend.GetLevel(id));
    try { backend.SetLevel(id, 3); throw new Exception("Unsafe overlay was re-enabled"); }
    catch (InvalidOperationException) { }
    Equal(0, backend.GetLevel(id));
    backend.VerifyNative();
});
if (args.Contains("--linux-worker") && OperatingSystem.IsLinux()) Run("Linux worker idle, crash reconnect and owned-process cleanup", () =>
{
    using var backend = new LinuxDimmingBackend(null, testKey: true, workerExecutable: linuxWorkerExecutable);
    Assert(backend.Displays.Count > 0, backend.Status);
    backend.VerifyNative();
    backend.RestoreAll();
    var recoveryVersion = backend.RestoreVersion;
    var originalId = backend.WorkerId;
    using (var child = Process.GetProcessById(originalId))
    {
        var before = child.TotalProcessorTime;
        var revision = backend.Revision;
        Thread.Sleep(2000);
        child.Refresh();
        var cpu = (child.TotalProcessorTime - before).TotalMilliseconds;
        Console.WriteLine($"  Undimmed worker idle CPU: {cpu:F1} ms over 2000 ms; publications: {backend.Revision - revision}.");
        Assert(cpu < 150, "Unexpected idle worker CPU activity");
        child.Kill(); child.WaitForExit();
    }
    Assert(SpinWait.SpinUntil(() => OperatingSystem.IsLinux() && backend.WorkerId != 0 && backend.WorkerId != originalId && backend.Displays.Count > 0, 10000), "Worker did not reconnect");
    Equal(recoveryVersion, backend.RestoreVersion);
    backend.VerifyNative();
    using var finalChild = Process.GetProcessById(backend.WorkerId);
    backend.Dispose();
    Assert(finalChild.WaitForExit(3000), "Owned overlay worker survived disposal");
});
if (args.Contains("--linux-native") && OperatingSystem.IsLinux()) Run("Linux isolated overlays, native bounds/input shape, recovery and idle", () =>
{
    using var backend = new LinuxDimmingBackend(null, testKey: true);
    Assert(backend.Displays.Count > 0, backend.Status);
    Assert(backend.Status.StartsWith("X11 overlays active."), backend.Status);
    backend.VerifyNative();
    var display = backend.Displays[0];
    backend.SetLevel(display.Id, 3);
    Equal(3, backend.GetLevel(display.Id));
    backend.VerifyNative();
    backend.Refresh();
    Equal(3, backend.GetLevel(display.Id));
    var workerId = backend.WorkerId;
    using (var worker = Process.GetProcessById(workerId))
    {
        var before = worker.TotalProcessorTime;
        var revision = backend.Revision;
        Thread.Sleep(2000);
        worker.Refresh();
        var cpu = (worker.TotalProcessorTime - before).TotalMilliseconds;
        Console.WriteLine($"  Worker idle CPU: {cpu:F1} ms over 2000 ms; state publications: {backend.Revision - revision}.");
        Assert(cpu < 150, "Unexpected idle worker CPU activity");
        backend.RestoreAll();
        foreach (var d in backend.Displays) Equal(0, backend.GetLevel(d.Id));
        backend.VerifyNative();
        worker.Kill(); worker.WaitForExit();
    }
    Assert(SpinWait.SpinUntil(() => OperatingSystem.IsLinux() && backend.WorkerId != 0 && backend.WorkerId != workerId && backend.Displays.Count > 0, 10000), "Worker did not reconnect");
    backend.VerifyNative();
    Equal(0, backend.GetLevel(display.Id));
    var finalId = backend.WorkerId;
    using var finalWorker = Process.GetProcessById(finalId);
    backend.Dispose();
    Assert(finalWorker.WaitForExit(3000), "Worker survived backend disposal");
});
if (args.Contains("--linux-catalog") && OperatingSystem.IsLinux()) Run("Linux XRandR read-only display probe", () =>
{
    var probe = X11DisplayCatalog.Probe();
    Assert(probe.Displays.Count > 0, "No displays found");
    Assert(probe.Displays.All(d => d.Width > 0 && d.Height > 0), "Invalid monitor bounds");
    Console.WriteLine($"  XRandR {probe.RandrMajor}.{probe.RandrMinor}; {probe.Displays.Count} surface(s); compositor selection present: {probe.Compositor}; trusted identities: {probe.Displays.Count(d => d.CanRemember)}.");
});
if (args.Contains("--mqtt")) Run("Isolated MQTT discovery, commands, credentials and reconnect", () => AutomationTests.Run().GetAwaiter().GetResult());
Run("Single instance rejects duplicates and recovers after exit or crash", SingleInstanceTests.Run);
Run("Command line parses controls, refuses invalid input and survives forwarding", CommandLineTests.Run);
Run("Forwarded command lines apply live and untrusted channel input is refused", () => InstanceChannelTests.Run().GetAwaiter().GetResult());
Run("Dimming range and alpha", () =>
{
    Equal((byte)0, DimLevel.Alpha(0)); Equal((byte)204, DimLevel.Alpha(80));
    Throws<ArgumentOutOfRangeException>(() => DimLevel.Validate(-1));
    Throws<ArgumentOutOfRangeException>(() => DimLevel.Validate(101));
});
Run("Wayland protocol hotplug, rotation and connection identity", () => WaylandCatalogTests.Run().GetAwaiter().GetResult());
Run("Linux desktop identity follows native session endpoints", () =>
{
    var runtime = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Shade.SessionIdentity"));
    var first = LinuxDesktopSession.Describe("test-user", ":0", "wayland-0", runtime, "wayland");
    var xwaylandChanged = LinuxDesktopSession.Describe("test-user", ":7", "wayland-0", runtime, "wayland");
    Equal(first, xwaylandChanged);
    Equal(first, LinuxDesktopSession.Describe("test-user", null, Path.Combine(runtime, "wayland-0"), null, "wayland"));
    Equal(first, LinuxDesktopSession.Describe("test-user", null, null, runtime, "wayland"));
    Assert(first != LinuxDesktopSession.Describe("test-user", ":0", "wayland-1", runtime, "wayland"), "Different Wayland desktops share a lock");
    Assert(first != LinuxDesktopSession.Describe("another-user", ":0", "wayland-0", runtime, "wayland"), "Different users share a lock");
    Assert(first != LinuxDesktopSession.Describe("test-user", ":0", "wayland-0", Path.Combine(runtime, "other"), "wayland"), "Different runtime sockets share a lock");
    var x11 = LinuxDesktopSession.Describe("test-user", ":0", null, runtime, "x11");
    Assert(!x11.UsesWayland && x11 != first, "Native backend selection and singleton identity disagree");
    Assert(x11 != LinuxDesktopSession.Describe("test-user", ":1", null, runtime, "x11"), "Different X11 displays share a lock");
    Assert(LinuxDesktopSession.Describe("test-user", ":0", null, runtime, null, "9").UsesWayland,
        "Inherited Wayland descriptor silently selected X11");
});
Run("Wayland layer lifecycle, input exclusion and idle protocol silence", () => WaylandLayerTests.Run().GetAwaiter().GetResult());
Run("CupriFace recovery setup action follows backend availability", () =>
{
    using var backend = new FakeBackend { RecoverySetupNeeded = true };
    var app = new ShadeApp(backend);
    using var document = app.CreateDocument();
    using (var frame = document.RenderToImage(720, 1200, app.Background)) { }
    var button = Find(document.Root, n => n.Element?.Matches(".recovery-setup") == true)!;
    Assert(button.Style.Display.ToString() != "None", "Recovery setup button is hidden");
    var box = HitTesting.ScreenBox(button);
    Assert(document.DispatchClick(box.X + box.W / 2, box.Y + box.H / 2), "Recovery setup click unhandled");
    Equal(1, backend.RecoverySetupCalls);
    document.Refresh();
    using (var frame = document.RenderToImage(720, 1200, app.Background)) { }
    Assert(Find(document.Root, n => n.Element?.Matches(".recovery-setup") == true)!.Style.Display.ToString() == "None", "Setup button remained visible after registration");
});
Run("Wayland output batches preserve logical geometry until done", () =>
{
    var state = new WaylandOutputState(4, 3);
    Assert(!state.Apply(false, 2, []), "Initial core done invented logical geometry");
    state.Apply(true, 0, WaylandWire.Words(unchecked((uint)-1280), 40));
    state.Apply(true, 1, WaylandWire.Words(1280, 720));
    state.Apply(false, 1, WaylandWire.Words(1, 1920, 1080, 60000));
    Assert(state.Current is null, "Partial output batch was published");
    Assert(!state.Apply(true, 2, []), "Deprecated xdg done committed a version 3 batch");
    Assert(state.Apply(false, 2, []), "Core done did not commit the version 3 batch");
    Equal(-1280, state.Current!.X); Equal(1280, state.Current.Width); Equal(1920, state.Current.PixelWidth);
    state.Apply(true, 1, WaylandWire.Words(720, 1280));
    Equal(1280, state.Current.Width);
    Assert(state.Apply(false, 2, []), "Rotation-sized logical update was not committed");
    Equal(720, state.Current.Width);
    Assert(!state.Apply(false, 2, []), "Unchanged batch generated a revision");
    var old = new WaylandOutputState(3, 2);
    old.Apply(true, 0, WaylandWire.Words(0, 0)); old.Apply(true, 1, WaylandWire.Words(800, 600));
    Assert(!old.Apply(false, 2, []) && old.Apply(true, 2, []), "Version 2 used the wrong completion event");
});
Run("X11 recovery follows remapped Alt and lock modifiers", () =>
{
    var map = new byte[16];
    map[6] = 10; map[12] = 11; // Alt keys in Mod1 and Mod4.
    map[2] = 30; map[10] = 20; map[8] = 21; // Caps, Num in Mod3, Scroll in Mod2.
    var masks = X11HotkeyModifiers.Build(map, 2, [10, 11], [20, 21, 30]);
    Equal(16, masks.Length);
    foreach (uint alt in new uint[] { 8, 64 })
        foreach (uint locks in new uint[] { 0, 2, 16, 18, 32, 34, 48, 50 })
            Assert(masks.Contains(1u | 4u | alt | locks), "Missing mapped Alt/lock combination");
    map[10] = 0; map[14] = 20; // Num Lock moves to Mod5.
    masks = X11HotkeyModifiers.Build(map, 2, [10, 11], [20, 21, 30]);
    Assert(masks.Any(m => (m & 128) != 0) && masks.All(m => (m & 32) == 0), "Stale Num Lock modifier survived remapping");
    Equal(0, X11HotkeyModifiers.Build(map, 2, [0], [20, 21, 30]).Length);
    Equal(0, X11HotkeyModifiers.Build(map, 2, [10], [10, 20, 30]).Length);
});
Run("Hardware identity ignores geometry and connection", () =>
{
    var key = MonitorIdentity.HardwareKey(Edid(1234))!;
    Assert(key.Length == 64, "Missing hardware ID");
    var first = Candidate("source-a", "port-a", key);
    var a = MonitorIdentity.Resolve([first], new HashSet<string>()).Single();
    var b = MonitorIdentity.Resolve([first with { Source = "source-z", Connection = "port-z", X = -1920, Width = 2560 }], new HashSet<string>()).Single();
    Equal(a.Id, b.Id); Assert(b.CanRemember, "Unique serial should be rememberable");
    Assert(MonitorIdentity.HardwareKey(Edid(4321)) != key, "Distinct serials collided");
});
Run("Malformed and missing EDID serials are not trusted", () =>
{
    Assert(MonitorIdentity.HardwareKey(Edid(0)) is null, "Zero serial accepted");
    var bad = Edid(123); bad[20] ^= 1;
    Assert(MonitorIdentity.HardwareKey(bad) is null, "Bad checksum accepted");
    Assert(MonitorIdentity.HardwareKey([1, 2, 3]) is null, "Short EDID accepted");
});
Run("Duplicate identity ledger prevents later mistaken recall", () =>
{
    var ledger = new HashSet<string>();
    var key = MonitorIdentity.HardwareKey(Edid(123))!;
    var a = Candidate("a", "port-a", key); var b = Candidate("b", "port-b", key);
    var displays = MonitorIdentity.Resolve([a, b], ledger);
    Equal(2, displays.Select(d => d.Id).Distinct().Count());
    Assert(displays.All(d => !d.CanRemember), "Duplicate serial was trusted");
    Assert(!MonitorIdentity.Resolve([a], ledger).Single().CanRemember, "Duplicate trusted after disconnect");
    var fresh = MonitorIdentity.Resolve([a with { HardwareKey = null }], new HashSet<string>()).Single();
    Assert(!fresh.CanRemember, "Missing serial was trusted");
});
Run("Mirrored paths produce one deterministic overlay identity", () =>
{
    var a = Candidate("shared", "a", "unique-a"); var b = Candidate("shared", "b", "unique-b");
    var x = MonitorIdentity.Resolve([a, b], new HashSet<string>()).Single();
    var y = MonitorIdentity.Resolve([b, a], new HashSet<string>()).Single();
    Equal(x.Id, y.Id); Assert(x.CanRemember, "Unique clone group not rememberable");
});
Run("Atomic settings round trip and corrupt-file preservation", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "settings.json");
    var store = new SettingsStore(path); var settings = new ShadeSettings();
    settings.Displays["monitor-v1-test"] = new(25, -1920, 0, 1920, 1080);
    settings.AmbiguousHardware.Add("duplicate-test");
    Assert(store.Save(settings), "Save failed");
    var loaded = new SettingsStore(path).Load();
    Equal(settings.Displays.Single().Value, loaded.Displays.Single().Value);
    Assert(loaded.AmbiguousHardware.Contains("duplicate-test"), "Collision ledger lost");
    File.WriteAllText(path, "{broken");
    var broken = new SettingsStore(path); Equal(0, broken.Load().Displays.Count);
    Assert(!broken.Save(new()), "Corrupt file overwritten"); Equal("{broken", File.ReadAllText(path));
    // Delete only the exact synthetic test file, then empty directories, never recursively.
    File.Delete(path); Directory.Delete(directory);
});
Run("Settings recover after a temporary write failure", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "settings.json");
    Directory.CreateDirectory(path); // An empty directory temporarily blocks the file destination.
    var store = new SettingsStore(path);
    try
    {
        Assert(!store.Save(new()), "Blocked destination unexpectedly saved");
        Assert(store.Error is not null, "Write failure was not reported");
        Directory.Delete(path);
        var settings = new ShadeSettings();
        settings.Displays["synthetic-monitor"] = new(15, 0, 0, 1920, 1080);
        Assert(store.Save(settings), "Saving did not recover after the obstruction was removed");
        Assert(store.Error is null, "Successful retry left a stale error");
        Equal(15, new SettingsStore(path).Load().Displays["synthetic-monitor"].Level);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(path)) Directory.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        Directory.Delete(directory);
    }
});
Run("Explicit settings recovery preserves original and rolls back failed replacement", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory); var path = Path.Combine(directory, "settings.json");
    try
    {
        File.WriteAllText(path, "{synthetic-broken");
        var store = new SettingsStore(path); store.Load();
        Assert(store.PreservingUnreadableFile, "Unreadable file not protected");
        Directory.CreateDirectory(path + ".tmp");
        Assert(!store.Recover(new()), "Blocked replacement unexpectedly succeeded");
        Equal("{synthetic-broken", File.ReadAllText(path));
        Assert(store.PreservingUnreadableFile, "Failed recovery removed preservation");
        Directory.Delete(path + ".tmp");
        Assert(store.Recover(new() { GlobalLevel = 42 }), "Recovery did not succeed after obstruction removed");
        Equal(42, new SettingsStore(path).Load().GlobalLevel);
        var backup = Directory.GetFiles(directory, "settings.json.backup-*").Single();
        Equal("{synthetic-broken", File.ReadAllText(backup));
        Assert(store.Error is null && !store.PreservingUnreadableFile, "Recovery left stale error state");
        File.Delete(backup);
    }
    finally
    {
        if (Directory.Exists(path + ".tmp")) Directory.Delete(path + ".tmp");
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        File.Delete(path); Directory.Delete(directory);
    }
});
Run("Settings migrate legacy levels and preserve future versions", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory); var path = Path.Combine(directory, "settings.json");
    try
    {
        File.WriteAllText(path, """{"Version":1,"Displays":{"a":{"Level":25,"X":0,"Y":0,"Width":800,"Height":600}}} """);
        var store = new SettingsStore(path); var settings = store.Load();
        Equal(4, settings.Version); Equal(new RememberedControl(true, false, 25), settings.Controls["a"]);
        Assert(store.Save(settings), "Migration could not save");
        Equal(settings.Controls["a"], new SettingsStore(path).Load().Controls["a"]);
        File.WriteAllText(path, """{"Version":99}""");
        store = new(path); store.Load(); Assert(!store.Save(new()), "Unknown version overwritten");
        File.WriteAllText(path, """{"Version":2,"Controls":{"a":{"IndividualLevel":81}}}""");
        store = new(path); Equal(0, store.Load().Controls.Count); Assert(store.Error is not null, "Invalid intent accepted");
    }
    finally { File.Delete(path); Directory.Delete(directory); }
});
Run("Global intent, exclusions and recovery remain independent", () =>
{
    using var backend = new FakeBackend(); var controls = new ScreenControls(backend);
    controls.SetGlobal(40); Equal(0, backend.GetLevel("a")); Equal(0, backend.GetLevel("b"));
    controls.Toggle("a"); controls.Toggle("b");
    controls.SetUseGlobal("b", false); controls.SetGlobal(50);
    Equal(50, backend.GetLevel("a")); Equal(40, backend.GetLevel("b"));
    controls.Disable("a"); controls.SetGlobal(60); Equal(0, backend.GetLevel("a"));
    controls.SetUseGlobal("a", false); controls.SetUseGlobal("a", true);
    Equal(0, backend.GetLevel("a")); controls.Toggle("a"); Equal(60, backend.GetLevel("a"));
    controls.SetIndividual("b", 25); Equal(60, controls.GlobalLevel);
    controls.SetGlobal(0); Assert(controls.Get("a").Enabled, "Zero global discarded enabled intent");
    Equal(25, backend.GetLevel("b")); controls.SetGlobal(35); Equal(35, backend.GetLevel("a"));
    controls.SetGlobal(0); backend.RestoreAll(); controls.SetGlobal(45);
    Equal(0, backend.GetLevel("a")); Equal(0, backend.GetLevel("b"));
    Assert(!controls.Get("a").Enabled, "Emergency restore at zero left screen enabled");
    backend.ReplaceDisplays([FakeBackend.InitialDisplays[1], new("c", "New screen", -800, 0, 800, 600), FakeBackend.InitialDisplays[0]]);
    controls.SetGlobal(55); Equal(0, backend.GetLevel("c"));
    Assert(!controls.Get("b").UseGlobal, "Exclusion followed display order instead of identity");
    controls.RestoreAll(); Equal(55, controls.GlobalLevel);
});
Run("TLS toggles switch standard ports and preserve custom input", () =>
{
    using var backend = new FakeBackend();
    var model = new ShadeModel(backend);
    foreach (var (tls, port, expected) in new[] {
        (false, "8883", "1883"), (true, "1883", "8883"),
        (false, "28883", "28883"), (true, "21883", "21883"),
        (false, "", ""), (true, "invalid", "invalid") })
    {
        model.SetBindable(nameof(ShadeModel.BrokerTls), !tls);
        model.SetBindable(nameof(ShadeModel.BrokerPort), port);
        model.SetBindable(nameof(ShadeModel.BrokerTls), tls);
        Equal(expected, model.BrokerPort);
    }
});
Run("Full shading is opt-in and disabling it clamps levels without enabling screens", () =>
{
    using var backend = new FakeBackend();
    var controls = new ScreenControls(backend);
    Equal(80, controls.Maximum);
    Throws<ArgumentOutOfRangeException>(() => controls.SetGlobal(100));
    controls.SetAllowFullShade(true); controls.SetGlobal(100);
    Equal(0, backend.GetLevel("a"));
    controls.Toggle("a"); Equal(100, backend.GetLevel("a")); Equal((byte)255, DimLevel.Alpha(100));
    controls.SetIndividual("a", 95); controls.SetAllowFullShade(false);
    Equal(80, controls.GlobalLevel); Equal(80, backend.GetLevel("a")); Equal(0, backend.GetLevel("b"));
    Assert(!controls.Get("a").UseGlobal, "Clamping changed global membership");
    var path = Path.Combine(Path.GetTempPath(), "shade-full-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        var settings = new ShadeSettings { AllowFullShade = true, GlobalLevel = 100 };
        settings.Controls["disconnected"] = new(true, false, 99);
        settings.Displays["disconnected"] = new(99, 0, 0, 1920, 1080);
        var store = new SettingsStore(path); Assert(store.Save(settings), "Full shading save failed");
        var restored = store.Load(); Assert(restored.AllowFullShade, "Full shading preference lost"); Equal(100, restored.GlobalLevel);
        restored.SetFullShade(false);
        Equal(80, restored.Controls["disconnected"].IndividualLevel); Equal(80, restored.Displays["disconnected"].Level);
        Assert(store.Save(restored) && !store.Load().AllowFullShade, "Reduced limit did not persist");
    }
    finally { File.Delete(path); }
});
Run("Home Assistant device name identifies the PC and respects the selected limit", () =>
{
    var protocol = new HomeAssistantProtocol("00000000000000000000000000000001", "TEST-PC");
    foreach (var maximum in new[] { 80, 100 })
    {
        var state = new AutomationState(30, [], maximum);
        using var config = System.Text.Json.JsonDocument.Parse(protocol.Build(state, new HashSet<string>()).First().Payload);
        Equal("TEST-PC Shade", config.RootElement.GetProperty("device").GetProperty("name").GetString());
        Equal(maximum, config.RootElement.GetProperty("max").GetInt32());
        Equal(maximum == 100, protocol.Parse(protocol.Root + "/global/set", "100", false, state) is not null);
    }
});
Run("CupriFace slider, independent restoration and keyboard", () =>
{
    using var backend = new FakeBackend();
    var app = new ShadeApp(backend);
    using var document = app.CreateDocument();
    void Render() { using var frame = document.RenderToImage(720, 1200, app.Background); }
    void Click(string selector, double ratio = 0.5)
    {
        Render();
        var node = Find(document.Root, n => n.Element?.Matches(selector) == true)
            ?? throw new Exception("Control missing: " + selector);
        var box = HitTesting.ScreenBox(node);
        Assert(document.DispatchClick(box.X + (float)(box.W * ratio), box.Y + box.H / 2), "Click unhandled");
        document.DispatchPointerUp(box.X + (float)(box.W * ratio), box.Y + box.H / 2);
    }
    Render();
    Assert(Find(document.Root, n => n.Element?.GetAttribute("id") == "advanced-panel")!.Style.Display.ToString() == "None", "Advanced was not collapsed");
    foreach (var fullShade in new[] { false, true, false })
    {
        ((ShadeModel)app.Model).SetBindable(nameof(ShadeModel.AllowFullShade), fullShade);
        document.Refresh();
        foreach (var preset in fullShade ? new[] { 0, 25, 50, 75, 100 } : new[] { 0, 20, 40, 60, 80 })
        {
            Click($".global-preset[data-set-value='{preset}']");
            Equal(preset, ((ShadeModel)app.Model).GlobalLevel);
            Equal(0, backend.GetLevel("a")); Equal(0, backend.GetLevel("b"));
        }
    }
    Click(".global-slider", 0.25);
    Equal(0, backend.GetLevel("a")); Equal(0, backend.GetLevel("b"));
    Click(".monitor[data-display='a']");
    Click(".monitor[data-display='b']");
    Assert(backend.GetLevel("a") > 0, "Enabling a screen did not apply global");
    Equal(backend.GetLevel("a"), backend.GetLevel("b"));
    var previous = backend.GetLevel("a");
    Click(".monitor[data-display='a']"); Equal(0, backend.GetLevel("a")); Equal(previous, backend.GetLevel("b"));
    Click(".monitor[data-display='a']"); Equal(previous, backend.GetLevel("a"));
    Click(".restore");
    Click(".advanced");
    Click(".use-global");
    Assert(Find(document.Root, n => n.Element?.Matches(".use-global") == true)!.Element!.GetAttribute("checked") != "true", "Switch did not unlink");
    Click(".monitor[data-display='a']");
    var unlinked = backend.GetLevel("a");
    Click(".global-preset[data-set-value='60']");
    Equal(60, ((ShadeModel)app.Model).GlobalLevel);
    Equal(unlinked, backend.GetLevel("a")); Equal(0, backend.GetLevel("b"));
    Click(".global-slider", 0.6); Equal(unlinked, backend.GetLevel("a"));
    Click(".use-global");
    Assert(backend.GetLevel("a") != unlinked, "Switch did not link to global");
    Click(".individual-slider", 0.8);
    Assert(backend.GetLevel("a") is >= 55 and <= 70, $"Slider did not write through to backend: {backend.GetLevel("a")}; {backend.GetLevel("b")}");
    Equal(0, backend.GetLevel("b"));
    backend.SetLevel("b", 35);
    Click(".reset[data-display='a']");
    Equal(0, backend.GetLevel("a")); Equal(35, backend.GetLevel("b"));
    Click(".restore"); Equal(0, backend.GetLevel("b"));
    Click(".individual-slider", 0.5);
    var before = backend.GetLevel("a");
    document.DispatchKey(null, EditKey.Right);
    Assert(backend.GetLevel("a") > before, $"Keyboard did not adjust focused slider: {before} -> {backend.GetLevel("a")}");
    Click(".allow-full-shade"); Render();
    Assert(Find(document.Root, n => n.Element?.Matches(".global-slider") == true)!.Element!.GetAttribute("max") == "100", "Global slider limit did not update");
    Assert(Find(document.Root, n => n.Element?.Matches(".individual-slider") == true)!.Element!.GetAttribute("max") == "100", "Individual slider limit did not update");
    Click(".global-slider", 0.99);
    Equal(100, ((ShadeModel)app.Model).GlobalLevel);
    Click(".allow-full-shade"); Equal(80, ((ShadeModel)app.Model).GlobalLevel);
    backend.ReplaceDisplays([new("c", "New display", -1280, 0, 1280, 720)]);
    document.Refresh(); Render();
    Assert(Find(document.Root, n => n.Element?.GetAttribute("data-display") == "c") is not null, "Hotplug row not updated");
    Assert(Find(document.Root, n => n.Element?.GetAttribute("data-display") == "a") is null, "Disconnected row remained");
    // App-only image uses synthetic monitor data; never captures the user's desktop.
    backend.ReplaceDisplays(FakeBackend.InitialDisplays);
    backend.SetLevel("a", 30); backend.SetLevel("b", 55); document.Refresh();
    Click(".allow-full-shade");
    var output = Path.GetFullPath("artifacts"); Directory.CreateDirectory(output);
    File.WriteAllText(Path.Combine(output, "controls-debug.json"), document.DebugDump(720, 1200));
    using var image = document.RenderToImage(720, 1200, app.Background);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(Path.Combine(output, "prototype-ui.png")); data.SaveTo(stream);
});
Run("About dialog opens, dismisses and routes the project link", () =>
{
    using var backend = new FakeBackend();
    var app = new ShadeApp(backend);
    var model = (ShadeModel)app.Model;
    using var document = app.CreateDocument();
    void Render() { using var frame = document.RenderToImage(720, 740, app.Background); }
    void Click(string selector)
    {
        Render();
        var node = Find(document.Root, n => n.Element?.Matches(selector) == true)
            ?? throw new Exception("Control missing: " + selector);
        var box = HitTesting.ScreenBox(node);
        Assert(document.DispatchClick(box.X + box.W / 2, box.Y + box.H / 2), "Click unhandled");
        document.DispatchPointerUp(box.X + box.W / 2, box.Y + box.H / 2);
        Render();
    }
    Click(".about-toggle");
    Assert(model.AboutOpen, "About did not open");
    Assert(Find(document.Root, n => n.Element?.GetAttribute("role") == "dialog") is not null, "Missing modal semantics");
    Equal("1.1.0", model.AppVersion);
    Equal("Shade", app.Title);
    string? navigated = null;
    bool external = false;
    document.Navigated += e => { navigated = e.Href; external = e.External; };
    Click(".project-link");
    Equal("https://github.com/Wixely/Shade", navigated);
    Assert(external, "Project link must open externally");
    using (var frame = document.RenderToImage(720, 740, app.Background))
    using (var data = frame.Encode(SKEncodedImageFormat.Png, 100))
    using (var stream = File.Create(Path.GetFullPath("artifacts/about-ui.png"))) data.SaveTo(stream);
    Click(".about-close");
    Assert(!model.AboutOpen, "Close did not dismiss About");
    Click(".about-toggle");
    document.DispatchKey(null, EditKey.Escape);
    Assert(!model.AboutOpen, "Escape did not dismiss About");
    Equal(0, backend.GetLevel("a"));
    Equal(0, backend.GetLevel("b"));
});
Run("Monitor buttons preserve geometry and scroll at small sizes", () =>
{
    Display[] arrangement = [new("portrait", "Portrait", -1080, -300, 1080, 1920), new("wide", "Wide", 0, 0, 2560, 1440)];
    var layout = MonitorLayout.Create(arrangement);
    Assert(layout.Tiles[0].X < layout.Tiles[1].X && layout.Tiles[0].Y < layout.Tiles[1].Y, "Negative coordinates lost");
    var tile = layout.Tiles[0];
    Assert(Math.Abs((tile.Width + 8) / (tile.Height + 8) - 1080d / 1920) < 0.001, "Portrait ratio distorted");
    using var backend = new FakeBackend();
    backend.ReplaceDisplays(arrangement);
    var app = new ShadeApp(backend); using var doc = app.CreateDocument();
    using (var frame = doc.RenderToImage(420, 500, app.Background)) { }
    var main = Find(doc.Root, n => n.Element?.Matches(".content-scroll") == true)!;
    Assert(main.MaxScrollY > 0, "Short window has no vertical scrolling");
    Assert(doc.DispatchWheel(200, 450, 350), "Overflow did not handle scrolling");
    Assert(main.ScrollY > 0, "Scroll position did not change");
    File.WriteAllText("artifacts/scroll-debug.json", doc.DebugDump(420, 500));
});
Run("Home Assistant settings recovery through CupriFace preserves unreadable data", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory); var path = Path.Combine(directory, "automation.json");
    const string invalid = "{\"PublishedScreens\":[null]}";
    File.WriteAllText(path, invalid);
    try
    {
        using var integration = new HomeAssistantIntegration(path);
        Assert(integration.SettingsNeedBackup && !integration.Enabled, "Malformed integration was not preserved and disabled");
        using var backend = new FakeBackend(); backend.SetLevel("a", 15);
        var app = new ShadeApp(backend, integration); ((ShadeModel)app.Model).ToggleAutomation();
        using var doc = app.CreateDocument();
        using (var frame = doc.RenderToImage(720, 1800, app.Background))
        {
            Directory.CreateDirectory("artifacts");
            using var png = frame.Encode(SKEncodedImageFormat.Png, 100);
            using var output = File.Create("artifacts/automation-recovery.png"); png.SaveTo(output);
        }
        var button = Find(doc.Root, n => n.Element?.Matches(".broker-recover") == true)!;
        Assert(button.Style.Display.ToString() != "None", "Integration recovery hidden");
        var box = HitTesting.ScreenBox(button);
        Assert(doc.DispatchClick(box.X + box.W / 2, box.Y + box.H / 2), "Recovery action not handled");
        Assert(!integration.SettingsNeedRecovery && !integration.Enabled, "Recovery failed or enabled integration");
        Equal(15, backend.GetLevel("a"));
        var backup = Directory.GetFiles(directory, "automation.json.backup-*").Single();
        Equal(invalid, File.ReadAllText(backup));
        Assert(!new AutomationSettingsStore(path).Load().Enabled, "Recovered integration enabled on restart");
        File.Delete(backup);
    }
    finally { File.Delete(path); Directory.Delete(directory); }
});
Run("Integration recovery rolls back failed replacement and forgetting reports failed writes", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory); var path = Path.Combine(directory, "automation.json");
    var temporary = path + ".tmp";
    try
    {
        File.WriteAllText(path, "{broken");
        var store = new AutomationSettingsStore(path); var settings = store.Load();
        Directory.CreateDirectory(temporary);
        Assert(!store.Recover(settings), "Recovery unexpectedly wrote through blocked temporary path");
        Equal("{broken", File.ReadAllText(path)); Equal(0, Directory.GetFiles(directory, "*.backup-*").Length);
        Directory.Delete(temporary);
        Assert(store.Recover(settings), "Recovery did not retry");
        foreach (var backup in Directory.GetFiles(directory, "*.backup-*")) File.Delete(backup);
        settings.ProtectedPassword = "synthetic-opaque-credential";
        Assert(store.Save(settings), "Fixture credential save failed");
        using var integration = new HomeAssistantIntegration(path);
        Directory.CreateDirectory(temporary);
        integration.ConfigureAsync(new("127.0.0.2", 1883, false, "replacement-user", ""), true, false).GetAwaiter().GetResult();
        Assert(!integration.Enabled && integration.HasPassword, "Failed configuration replaced previous credential state");
        Equal(settings.Host, integration.Host);
        Equal(settings.ProtectedPassword, new AutomationSettingsStore(path).Load().ProtectedPassword);
        integration.ForgetPasswordAsync().GetAwaiter().GetResult();
        Assert(integration.HasPassword && integration.Status.Contains("saved password remains"), "Failed deletion claimed password was forgotten");
        Equal(settings.ProtectedPassword, new AutomationSettingsStore(path).Load().ProtectedPassword);
        Directory.Delete(temporary);
        integration.ForgetPasswordAsync().GetAwaiter().GetResult();
        Assert(!integration.HasPassword, "Forget retry failed");
        Equal("", new AutomationSettingsStore(path).Load().ProtectedPassword);
    }
    finally { File.Delete(path); if (Directory.Exists(temporary)) Directory.Delete(temporary); Directory.Delete(directory); }
});
Run("Home Assistant form binds text, password and TLS controls", () =>
{
    using var backend = new FakeBackend(); using var integration = new HomeAssistantIntegration();
    var app = new ShadeApp(backend, integration); var model = (ShadeModel)app.Model;
    using var doc = app.CreateDocument();
    void Click(string selector)
    {
        using var frame = doc.RenderToImage(720, 2000, app.Background);
        var node = Find(doc.Root, n => n.Element?.Matches(selector) == true)!;
        var box = HitTesting.ScreenBox(node);
        Assert(doc.DispatchClick(box.X + box.W / 2, box.Y + box.H / 2), "Form click unhandled");
        doc.DispatchPointerUp(box.X + box.W / 2, box.Y + box.H / 2);
    }
    using (var frame = doc.RenderToImage(720, 2000, app.Background)) { }
    CupriFace.Accessibility.AccessibilityNode? Accessible(CupriFace.Accessibility.AccessibilityNode node)
        => node.Role == "button" && node.Name == "Home Assistant" ? node : node.Children.Select(Accessible).FirstOrDefault(n => n is not null);
    var disclosure = Accessible(doc.BuildAccessibilityTree(720, 2000))!;
    Assert(doc.AccessibilityActivate(disclosure.Path), "Integration disclosure accessibility activation failed");
    Assert(model.AutomationOpen, "Integration disclosure click failed");
    Click("#broker-host"); doc.DispatchKey("localhost", EditKey.None); Equal("localhost", model.BrokerHost);
    Click("#broker-user"); doc.DispatchKey("synthetic-user", EditKey.None); Equal("synthetic-user", model.BrokerUsername);
    Click("#broker-password"); doc.DispatchKey("synthetic-password", EditKey.None); Equal("synthetic-password", model.BrokerPassword);
    CupriFace.Accessibility.AccessibilityNode? Field(CupriFace.Accessibility.AccessibilityNode node, string name)
        => node.Role == "textbox" && node.Name == name ? node : node.Children.Select(child => Field(child, name)).FirstOrDefault(child => child is not null);
    var hostPath = Field(doc.BuildAccessibilityTree(720, 2000), "Broker hostname")!.Path;
    Assert(doc.AccessibilityFocus(hostPath), "Accessible hostname focus rejected");
    Assert(doc.AccessibilitySetText(hostPath, "replacement.example"), "Accessible hostname replacement rejected");
    Equal("replacement.example", model.BrokerHost);
    Equal("replacement.example", Field(doc.BuildAccessibilityTree(720, 2000), "Broker hostname")!.Value);
    doc.DispatchKey("x", EditKey.None);
    Equal("replacement.examplex", model.BrokerHost);
    Click("#broker-user");
    Equal("replacement.examplex", model.BrokerHost);
    foreach (var attribute in new[] { "disabled", "aria-disabled", "readonly", "aria-readonly" })
    {
        var hostNode = doc.NodeAtPath(hostPath)!;
        hostNode.Element!.SetAttribute(attribute, "true");
        Assert(!doc.AccessibilitySetText(hostPath, "must-not-change"), "Accessible text bypassed " + attribute);
        Equal("replacement.examplex", model.BrokerHost);
        hostNode.Element.RemoveAttribute(attribute);
    }
    Assert(doc.AccessibilitySetText(hostPath, "restored.example"), "Editable state did not recover");
    Equal("restored.example", model.BrokerHost);
    Click("cupri-switch[aria-label='Use TLS']"); Assert(!model.BrokerTls, "TLS switch binding failed");
    Equal("1883", model.BrokerPort);
    Click("cupri-switch[aria-label='Use TLS']"); Equal("8883", model.BrokerPort);
    model.SetBindable(nameof(ShadeModel.BrokerPassword), ""); doc.Refresh();
    using var image = doc.RenderToImage(720, 2000, app.Background);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100); using var output = File.Create("artifacts/automation-ui.png"); data.SaveTo(output);
});
Run("Scrolled focus outline placement and clipping", FocusRenderingTests.Run);
Run("Accessible activation reveals scrollable targets without clicking clipped alternatives", FocusRenderingTests.AccessibilityActivation);
Run("Accessible focus reveals controls in both scroll axes", FocusRenderingTests.AccessibilityScroll);
Run("Accessible editor snapshots and Unicode selection preserve field boundaries", FocusRenderingTests.AccessibleTextSelection);
Run("Accessible text preserves whitespace, Unicode and empty placeholder fields", () =>
{
    var app = new AccessibilityStateApp();
    using var doc = app.CreateDocument();
    CupriFace.Accessibility.AccessibilityNode? Field(CupriFace.Accessibility.AccessibilityNode node)
        => node.Role == "textbox" ? node : node.Children.Select(Field).FirstOrDefault(child => child is not null);
    foreach (var value in new[] { "  padded text  ", "   ", "", "caf\u00e9\u03a9\U0001f642" })
    {
        app.SetBindable("Value", value); doc.Refresh();
        Equal(value, Field(doc.BuildAccessibilityTree(620, 400))!.Value);
    }
});
Run("Keyboard focus scrolls both axes and reverses", FocusRenderingTests.KeyboardScroll);
Run("Unchanged model requests no periodic rendering", () =>
{
    using var backend = new FakeBackend(); var app = new ShadeApp(backend);
    using var doc = app.CreateDocument();
    using var frame = doc.RenderToImage(680, 850, app.Background);
    app.Present(680, 850); Equal(0d, app.RefreshIntervalSeconds);
    backend.SetLevel("a", 10); Assert(app.RefreshIntervalSeconds > 0, "Backend change did not request refresh");
    doc.Refresh(); app.Present(680, 850); Equal(0d, app.RefreshIntervalSeconds);
});

if (args.Contains("--persistence") && OperatingSystem.IsWindows())
{
    Run("Native global batch validates all targets and publishes once", () =>
    {
        using var backend = new WindowsDimmingBackend(null, _ => [new("batch-a", "Synthetic A", 10, 10, 80, 80),
            new("batch-b", "Synthetic B", 100, 10, 80, 80)], 0x7A);
        var before = backend.Revision;
        backend.SetLevels(new Dictionary<string, int> { ["batch-a"] = 3, ["batch-b"] = 4 });
        Equal(before + 1, backend.Revision);
        Equal(3, backend.GetLevel("batch-a")); Equal(4, backend.GetLevel("batch-b"));
        backend.VerifyNativeState();
        before = backend.Revision;
        backend.SetLevels(new Dictionary<string, int> { ["batch-a"] = 3, ["batch-b"] = 4 });
        Equal(before, backend.Revision);
        try
        {
            backend.SetLevels(new Dictionary<string, int> { ["batch-a"] = 5, ["disconnected"] = 5 });
            throw new Exception("Stale batch unexpectedly succeeded");
        }
        catch (ArgumentException) { }
        Equal(3, backend.GetLevel("batch-a")); Equal(4, backend.GetLevel("batch-b"));
        backend.VerifyNativeState();
    });
    Run("Unreadable settings recovery through CupriFace keeps shading and backup", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "{synthetic-unreadable");
        try
        {
            using (var backend = new WindowsDimmingBackend(path, _ => [new("safe-test", "Synthetic", 10, 10, 100, 100, true)], 0x7A))
            {
                backend.SetLevel("safe-test", 3);
                var app = new ShadeApp(backend); using var doc = app.CreateDocument();
                using var image = doc.RenderToImage(720, 1200, app.Background);
                var button = Find(doc.Root, n => n.Element?.Matches(".settings-recover") == true)!;
                Assert(button.Style.Display.ToString() != "None", "Recovery action hidden");
                var box = HitTesting.ScreenBox(button);
                Assert(doc.DispatchClick(box.X + box.W / 2, box.Y + box.H / 2), "Recovery click unhandled");
                Assert(!backend.SettingsNeedRecovery, "UI recovery did not clear failure");
                Equal(3, backend.GetLevel("safe-test")); backend.VerifyNativeState();
            }
            Equal(3, new SettingsStore(path).Load().Displays["safe-test"].Level);
            var backup = Directory.GetFiles(directory, "settings.json.backup-*").Single();
            Equal("{synthetic-unreadable", File.ReadAllText(backup)); File.Delete(backup);
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    });
    Run("Assigned identity UI, reconnect, reassignment and detach", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Display a = new("connection-a", "Unknown A", 10, 10, 100, 100);
        Display b = new("connection-b", "Unknown B", 120, 10, 100, 100);
        IReadOnlyList<Display> topology = [a, b];
        string assigned;
        try
        {
            using (var backend = new WindowsDimmingBackend(path, _ => topology, 0x7A))
            {
                var app = new ShadeApp(backend); var model = (ShadeModel)app.Model;
                using var doc = app.CreateDocument(); model.ToggleAdvanced(); doc.Refresh();
                void Click(string selector)
                {
                    using var frame = doc.RenderToImage(720, 1800, app.Background);
                    var node = Find(doc.Root, n => n.Element?.Matches(selector) == true) ?? throw new Exception("Missing " + selector);
                    var box = HitTesting.ScreenBox(node);
                    Assert(doc.DispatchClick(box.X + box.W / 2, box.Y + box.H / 2), "Assignment control unhandled");
                    doc.DispatchPointerUp(box.X + box.W / 2, box.Y + box.H / 2);
                }
                Click(".assign-screen[data-display='connection-a']");
                Assert(model.AssignmentOpen, "Identity page did not open");
                Click(".assignment-name"); doc.DispatchKey("Left portrait", EditKey.None);
                Equal("Left portrait", model.AssignmentName);
                File.WriteAllText("artifacts/assignment-debug.json", doc.DebugDump(720, 900));
                using (var frame = doc.RenderToImage(720, 900, app.Background))
                using (var png = frame.Encode(SKEncodedImageFormat.Png, 100))
                using (var output = File.Create("artifacts/assignment-ui.png")) png.SaveTo(output);
                Click(".assignment-create");
                Assert(!model.AssignmentOpen, "Assignment failed: " + model.AssignmentStatus);
                assigned = backend.Displays.Single(d => d.Label == "Left portrait").Id;
                Assert(assigned.StartsWith("assigned-v1-"), "Persistent assignment ID missing");
                Equal(0, backend.GetLevel(assigned));
                IDimmingBackend assignmentBackend = backend;
                Throws<AssignmentException>(() => assignmentBackend.AssignScreen(b.Id, "Left portrait", assigned));
                model.SetBindable("Level_" + assigned, 4);
                topology = [b]; backend.RefreshTopology();
                Assert(!backend.Assignments.Single().Connected, "Disconnected assignment marked connected");
                model.OpenAssignment(b.Id); doc.Refresh();
                Click(".assignment-reuse[data-assignment='" + assigned + "']");
                Equal(assigned, backend.Displays.Single().Id); Equal(0, backend.GetLevel(assigned));
                model.Toggle(assigned); Equal(4, backend.GetLevel(assigned));
                backend.VerifyNativeState();
            }
            using (var backend = new WindowsDimmingBackend(path, _ => topology, 0x7A))
            {
                Equal(assigned, backend.Displays.Single().Id); Equal(4, backend.GetLevel(assigned));
                backend.DetachAssignment(assigned);
                Equal(b.Id, backend.Displays.Single().Id); Equal(0, backend.GetLevel(b.Id));
                Assert(!backend.Displays.Single().CanRemember, "Detached connection still trusted");
                backend.VerifyNativeState();
            }
            var saved = new SettingsStore(path).Load();
            Equal(3, saved.Version); Equal("", saved.Assignments[assigned].ConnectionId);
            Assert(!saved.Controls[assigned].Enabled, "Detach left remembered dimming active");
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    });
    Run("Native preferences survive restart, reconnect and zero-level recovery", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Display a = new("synthetic-a", "A", 10, 10, 100, 100, true);
        Display b = new("synthetic-b", "B", 120, 10, 100, 100, true);
        Display c = new("synthetic-c", "Ambiguous", 230, 10, 100, 100);
        IReadOnlyList<Display> topology = [a, b, c];
        try
        {
            using (var backend = new WindowsDimmingBackend(path, _ => topology, 0x7A))
            {
                Assert(!backend.Status.Contains("unavailable"), "Test recovery key unavailable");
                var controls = new ScreenControls(backend);
                controls.SetGlobal(3); controls.Toggle(a.Id); controls.SetIndividual(b.Id, 4);
                controls.SetUseGlobal(c.Id, false);
                topology = [b, c]; backend.RefreshTopology(); controls.SetGlobal(5);
                topology = [c, b, a with { X = 20, Width = 110 }]; backend.RefreshTopology();
                Equal(5, backend.GetLevel(a.Id)); Equal(4, backend.GetLevel(b.Id));
                controls.SetGlobal(0); controls.Disable(b.Id);
                backend.VerifyNativeState();
            }
            using (var backend = new WindowsDimmingBackend(path, _ => topology, 0x7A))
            {
                var controls = new ScreenControls(backend);
                Equal(0, controls.GlobalLevel); Assert(controls.Get(a.Id).Enabled, "Enabled-at-zero intent lost");
                Assert(!controls.Get(b.Id).Enabled && !controls.Get(b.Id).UseGlobal, "Disabled exclusion lost");
                Equal(4, controls.Get(b.Id).IndividualLevel);
                Assert(controls.Get(c.Id).UseGlobal, "Ambiguous identity was automatically recalled");
                controls.SetGlobal(6); Equal(6, backend.GetLevel(a.Id)); Equal(0, backend.GetLevel(b.Id));
                controls.Toggle(b.Id); Equal(4, backend.GetLevel(b.Id));
                topology = [b, c]; backend.RefreshTopology(); backend.TriggerRecovery();
                controls.SetGlobal(7); topology = [a, b, c]; backend.RefreshTopology();
                Equal(0, backend.GetLevel(a.Id)); Equal(0, backend.GetLevel(b.Id));
                backend.VerifyNativeState();
            }
            var saved = new SettingsStore(path).Load();
            Equal(7, saved.GlobalLevel); Assert(!saved.Controls[a.Id].Enabled, "Detached recovery not saved");
            Assert(!saved.Controls.ContainsKey(c.Id), "Untrusted identity persisted");
            Equal(100, saved.Displays[a.Id].Width); // Latest geometry is separate from identity.
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    });
}
if (args.Contains("--native") && OperatingSystem.IsWindows())
{
    Run("Windows physical catalog and native overlay smoke", () =>
    {
        using var backend = new WindowsDimmingBackend();
        Assert(backend.Displays.Count > 0, "No physical displays found; native test unavailable");
        Assert(!backend.Status.Contains("unavailable"), "Recovery key unavailable");
        var foreground = WindowsNative.GetForegroundWindow();
        for (var i = 0; i < backend.Displays.Count; i++) backend.SetLevel(backend.Displays[i].Id, 3 + i % 4);
        backend.VerifyNativeState();
        Equal(foreground, WindowsNative.GetForegroundWindow());
        var current = Process.GetCurrentProcess(); var start = current.TotalProcessorTime;
        var revision = backend.Revision; var elapsed = Stopwatch.StartNew(); Thread.Sleep(3000);
        var cpuMs = (current.TotalProcessorTime - start).TotalMilliseconds;
        Equal(revision, backend.Revision);
        Console.WriteLine($"  Overlay idle: {cpuMs:F1} process CPU ms / {elapsed.Elapsed.TotalMilliseconds:F0} wall ms; {backend.Displays.Count} display(s).");
        backend.TriggerRecovery(); backend.VerifyNativeState();
        foreach (var d in backend.Displays) Equal(0, backend.GetLevel(d.Id));
        using var conflicting = new WindowsDimmingBackend();
        Assert(conflicting.Status.Contains("hotkey unavailable"), "Hotkey conflict not surfaced");
        IDimmingBackend conflictingBackend = conflicting;
        Throws<InvalidOperationException>(() => conflictingBackend.SetLevel(conflictingBackend.Displays[0].Id, 3));
        conflicting.VerifyNativeState();
    });
    Run("Native automatic add, resize, move, remove and reconnect", () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "Shade.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var failCatalog = false;
        IReadOnlyList<Display> topology = [new("synthetic-a", "Synthetic A", 10, 10, 320, 240, true)];
        using (var backend = new WindowsDimmingBackend(path, _ => failCatalog ? throw new InvalidOperationException("Synthetic transition") : topology))
        {
            backend.SetLevel("synthetic-a", 4);
            topology = [topology[0] with { X = 20, Width = 400 }, new("synthetic-b", "Synthetic B", 430, 10, 200, 200, true)];
            backend.RefreshTopology(); backend.VerifyNativeState();
            Equal(4, backend.GetLevel("synthetic-a")); Equal(0, backend.GetLevel("synthetic-b"));
            backend.SetLevel("synthetic-b", 6);
            var first = topology[0]; topology = [topology[1]];
            backend.RefreshTopology(); Equal(0, backend.GetLevel("synthetic-a")); Equal(6, backend.GetLevel("synthetic-b"));
            topology = [topology[0], first with { Y = 30, Height = 260 }];
            backend.RefreshTopology(); backend.VerifyNativeState(); Equal(4, backend.GetLevel("synthetic-a"));
            failCatalog = true; backend.RefreshTopology();
            Equal(0, backend.GetLevel("synthetic-a")); backend.VerifyNativeState();
            failCatalog = false;
            Assert(SpinWait.SpinUntil(() => ((IDimmingBackend)backend).GetLevel("synthetic-a") == 4, 3000), "Transient topology did not recover automatically");
            backend.VerifyNativeState();
        }
        using (var backend = new WindowsDimmingBackend(path, _ => topology))
        {
            Equal(4, backend.GetLevel("synthetic-a")); Equal(6, backend.GetLevel("synthetic-b"));
            backend.RestoreAll(); backend.VerifyNativeState();
        }
        File.Delete(path); Directory.Delete(directory);
    });
}

Console.WriteLine(failed == 0 ? "All checks passed." : $"{failed} check(s) failed.");
return failed == 0 ? 0 : 1;

void Run(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}");
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static MonitorCandidate Candidate(string source, string connection, string? hardware) => new(source, connection, "Synthetic display", hardware, 0, 0, 1920, 1080);
static byte[] Edid(uint serial)
{
    var bytes = new byte[128]; new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
    bytes[8] = 0x10; bytes[9] = 0xac; bytes[10] = 1; bytes[18] = 1; bytes[19] = 4;
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), serial);
    bytes[127] = unchecked((byte)-bytes.Sum(x => x)); return bytes;
}
static RenderNode? Find(RenderNode node, Func<RenderNode, bool> predicate)
{
    if (predicate(node)) return node;
    foreach (var child in node.Children) if (Find(child, predicate) is { } found) return found;
    return null;
}
sealed class FakeBackend : IDimmingBackend
{
    public bool RecoverySetupNeeded { get; set; }
    public int RecoverySetupCalls { get; private set; }
    public void ConfigureRecovery() { RecoverySetupCalls++; RecoverySetupNeeded = false; Revision++; }
    public static Display[] InitialDisplays => [new("a", "Display 1", 0, 0, 1920, 1080), new("b", "Display 2", 1920, 0, 2560, 1440)];
    public IReadOnlyList<Display> Displays { get; private set; } = InitialDisplays;
    private readonly Dictionary<string, int> levels = [];
    public string Status => "Recovery: Ctrl+Alt+Shift+R restores every display.";
    public long Revision { get; private set; }
    public long RestoreVersion { get; private set; }
    public int GetLevel(string id) => levels.GetValueOrDefault(id);
    public void SetLevel(string id, int level) { levels[id] = DimLevel.Validate(level); Revision++; }
    public void RestoreAll() { levels.Clear(); Revision++; RestoreVersion++; }
    public void ReplaceDisplays(IReadOnlyList<Display> displays) { Displays = displays; levels.Clear(); Revision++; }
    public void Dispose() { }
}
