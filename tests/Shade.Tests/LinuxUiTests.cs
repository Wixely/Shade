using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Shade;

[SupportedOSPlatform("linux")]
internal static class LinuxUiTests
{
    // A real published host on X11/Xwayland. Only windows with the owned child's PID
    // are resized/closed; this does not synthesize global keyboard or pointer input.
    internal static async Task Run(string executable, string evidence, bool software, bool interact = false, bool keyringUnavailable = false)
    {
        evidence = Path.GetFullPath(evidence);
        Directory.CreateDirectory(evidence);
        var capture = Path.Combine(evidence, "controls.png");
        if (File.Exists(capture)) throw new IOException("Use a new evidence directory.");
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        string? automationPath = null;
        byte[]? priorSettings = null;
        if (keyringUnavailable)
        {
            Check(interact, "Unavailable-keyring UI check requires --interact");
            if (NativeLibrary.TryLoad("libsecret-1.so.0", out var secret))
            {
                NativeLibrary.Free(secret);
                throw new Exception("Unavailable-keyring fixture requires an environment without libsecret; no store operation attempted");
            }
            var settings = Path.Combine(evidence, "settings");
            Check(!Directory.Exists(settings), "Use a fresh isolated settings directory");
            automationPath = Path.Combine(settings, "automation.json");
            Check(new AutomationSettingsStore(automationPath).Save(new AutomationSettings
            {
                Enabled = false, Host = "previous.invalid", Port = 8883, Tls = true,
                Username = "previous-user", ProtectedPassword = "synthetic-previous-opaque-value"
            }), "Could not seed isolated broker settings");
            priorSettings = await File.ReadAllBytesAsync(automationPath);
            start.ArgumentList.Add("--settings-directory"); start.ArgumentList.Add(settings);
        }
        else start.ArgumentList.Add("--ephemeral");
        start.ArgumentList.Add("--no-tray"); start.ArgumentList.Add("--diagnostics");
        start.Environment.Remove("WAYLAND_DISPLAY"); start.Environment.Remove("WAYLAND_SOCKET");
        start.Environment["XDG_SESSION_TYPE"] = "x11";
        start.Environment["SDL_VIDEODRIVER"] = "x11";
        if (software) start.Environment["CUPRIFACE_SOFTWARE"] = "1";
        else start.Environment.Remove("CUPRIFACE_SOFTWARE");
        start.Environment["CUPRIFACE_FRAME_DUMP"] = capture;
        X11Native.XInitThreads();
        // A window can disappear between enumeration and a property/geometry query.
        // Keep Xlib's default error handler from terminating this isolated driver.
        X11Native.ErrorHandler errors = (_, _) => 0;
        var previousHandler = X11Native.XSetErrorHandler(errors);
        var display = X11Native.XOpenDisplay(0);
        Check(display != 0, "No X11 display for UI test");
        try
        {
            using var child = Process.Start(start) ?? throw new Exception("UI child did not start");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(interact ? 120 : 40));
                // Native X11 calls do not observe managed cancellation. Stop the owned
                // renderer independently if its resize loop starves the X server.
                using var watchdog = deadline.Token.Register(() =>
                {
                    try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                });
                nuint window = 0;
                while (window == 0)
                {
                    Check(!child.HasExited, "Published UI exited before creating its window");
                    window = FindWindow(display, child.Id);
                    await Task.Delay(50, deadline.Token);
                }
                // Cupri's software debug readback occurs every 15 presentations.
                for (var i = 0; i < 45; i++)
                {
                    XResizeWindow(display, window, (uint)(720 + i % 2 * 8), 740);
                    X11Native.XSync(display, 0);
                    await Task.Delay(150, deadline.Token);
                }
                if (software) Check(File.Exists(capture) && new FileInfo(capture).Length > 1024, "No Cupri software frame readback");
                await Task.Delay(500, deadline.Token);
                CheckGeometry(display, window);
                child.Refresh(); var cpu = child.TotalProcessorTime;
                for (var i = 0; i < 4; i++)
                {
                    await Task.Delay(500, deadline.Token);
                    CheckGeometry(display, window);
                }
                child.Refresh(); Console.WriteLine("Published Linux UI idle CPU ms / 2 seconds: " + (child.TotalProcessorTime - cpu).TotalMilliseconds);
                if (interact) await LinuxAccessibilityTests.Run(child.Id, deadline.Token, software ? async filename =>
                {
                    var before = File.GetLastWriteTimeUtc(capture);
                    for (var i = 0; i < 18; i++)
                    {
                        XResizeWindow(display, window, (uint)(720 + i % 2 * 8), 740);
                        X11Native.XSync(display, 0);
                        await Task.Delay(150, deadline.Token);
                    }
                    XResizeWindow(display, window, 720, 740); X11Native.XSync(display, 0);
                    Check(File.GetLastWriteTimeUtc(capture) > before, "No fresh Cupri interaction readback");
                    File.Copy(capture, Path.Combine(evidence, filename));
                } : null, async text =>
                {
                    // Send directly to the owned window, never to global keyboard focus.
                    SendKey(display, window, 0xffe3, 0, true); // Control_L
                    SendKey(display, window, 'a', 4, true);
                    SendKey(display, window, 'a', 4, false);
                    SendKey(display, window, 0xffe3, 4, false);
                    await Task.Delay(60, deadline.Token);
                    foreach (var ch in text)
                    {
                        Check(ch is >= ' ' and <= '~' && !char.IsUpper(ch), "Test typing supports unshifted ASCII only");
                        SendKey(display, window, ch, 0, true);
                        SendKey(display, window, ch, 0, false);
                        await Task.Delay(25, deadline.Token);
                    }
                    await Task.Delay(150, deadline.Token);
                }, async reverse =>
                {
                    if (reverse) SendKey(display, window, 0xffe1, 0, true);
                    SendKey(display, window, 0xff09, reverse ? 1u : 0u, true);
                    SendKey(display, window, 0xff09, reverse ? 1u : 0u, false);
                    if (reverse) SendKey(display, window, 0xffe1, 1, false);
                    await Task.Delay(80, deadline.Token);
                }, keyringUnavailable);
                var close = new X11Native.Event
                {
                    Type = 33, Display = display, Window = window, Format = 32,
                    MessageType = X11Native.XInternAtom(display, "WM_PROTOCOLS", 0),
                    Data = (nint)X11Native.XInternAtom(display, "WM_DELETE_WINDOW", 0)
                };
                Check(X11Native.XSendEvent(display, window, 0, 0, ref close) != 0, "UI close request failed");
                X11Native.XFlush(display);
                await child.WaitForExitAsync(deadline.Token);
                Check(child.ExitCode == 0, "Published UI normal close failed");
                if (automationPath is not null)
                {
                    var saved = await File.ReadAllBytesAsync(automationPath);
                    Check(priorSettings!.SequenceEqual(saved), "Failed keyring save changed previous broker settings");
                    Console.WriteLine("PASS failed keyring save preserves previous broker settings byte for byte");
                }
                if (!software) Check(!(await stdout).Contains("using the SDL software window", StringComparison.Ordinal), "Default host fell back to software");
                Console.WriteLine("PASS published Linux " + (software ? "software" : "default") + " host: window, repeated resize and normal close");
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                await File.WriteAllTextAsync(Path.Combine(evidence, "application.stdout.log"), await stdout);
                await File.WriteAllTextAsync(Path.Combine(evidence, "application.stderr.log"), await stderr);
            }
        }
        finally { X11Native.XCloseDisplay(display); XSetErrorHandler(previousHandler); GC.KeepAlive(errors); }
    }

    private static nuint FindWindow(nint display, int process)
    {
        var pending = new Queue<nuint>(); pending.Enqueue(X11Native.XDefaultRootWindow(display));
        var pid = X11Native.XInternAtom(display, "_NET_WM_PID", 0);
        var visited = 0;
        while (pending.TryDequeue(out var window) && visited++ < 4096)
        {
            if (XGetWindowProperty(display, window, pid, 0, 1, 0, 6, out _, out var format, out var count, out _, out var data) == 0)
            {
                try
                {
                    if (format == 32 && count == 1 && data != 0 && Marshal.ReadInt32(data) == process)
                    {
                        if (XFetchName(display, window, out var name) != 0)
                        {
                            try { if (Marshal.PtrToStringUTF8(name) == "Shade") return window; }
                            finally { if (name != 0) X11Native.XFree(name); }
                        }
                    }
                }
                finally { if (data != 0) X11Native.XFree(data); }
            }
            if (XQueryTree(display, window, out _, out _, out var children, out var length) != 0)
            {
                try { for (var i = 0; i < length; i++) pending.Enqueue((nuint)Marshal.ReadIntPtr(children, i * IntPtr.Size)); }
                finally { if (children != 0) X11Native.XFree(children); }
            }
        }
        return 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void SendKey(nint display, nuint window, nuint symbol, uint state, bool press)
    {
        var code = X11Native.XKeysymToKeycode(display, symbol);
        Check(code != 0, "Test key is unavailable in current layout");
        var key = new KeyEvent { Type = press ? 2 : 3, Display = display, Window = window,
            Root = X11Native.XDefaultRootWindow(display), State = state, Keycode = code, SameScreen = 1 };
        Check(XSendEvent(display, window, 0, press ? 1 : 2, ref key) != 0, "Targeted key event failed");
        X11Native.XFlush(display);
    }
    // Xlib LP64 XKeyEvent inside the 192-byte XEvent union.
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct KeyEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(24)] public nint Display;
        [FieldOffset(32)] public nuint Window;
        [FieldOffset(40)] public nuint Root;
        [FieldOffset(80)] public uint State;
        [FieldOffset(84)] public uint Keycode;
        [FieldOffset(88)] public int SameScreen;
    }
    [DllImport("libX11.so.6")] private static extern int XSendEvent(nint display, nuint window, int propagate, nint mask, ref KeyEvent value);
    private static void CheckGeometry(nint display, nuint window)
    {
        Check(X11Native.XGetGeometry(display, window, out _, out _, out _, out var width, out var height, out _, out _) != 0,
            "Could not read control window geometry");
        Check(width == 720 && height == 740, "Control window changed size after resize requests stopped");
    }
    [DllImport("libX11.so.6")] private static extern int XQueryTree(nint display, nuint window, out nuint root, out nuint parent, out nint children, out uint count);
    [DllImport("libX11.so.6")] private static extern int XFetchName(nint display, nuint window, out nint name);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(nint display, nuint window, nuint property, nint offset, nint length, int delete, nuint requestedType, out nuint actualType, out int format, out nuint count, out nuint remaining, out nint data);
    [DllImport("libX11.so.6")] private static extern int XResizeWindow(nint display, nuint window, uint width, uint height);
    [DllImport("libX11.so.6")] private static extern nint XSetErrorHandler(nint handler);
}
