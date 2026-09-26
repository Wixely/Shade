using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Shade;

// Shade is a windows-subsystem application, so starting it from Explorer, a shortcut or the tray
// never creates a console window. Hiding one after the fact is too late: the window is created by
// the loader before any code of ours runs, so it appears and then vanishes, which looks broken.
//
// Its command line still has to answer in a terminal. When Shade is started from one, it attaches to
// that terminal's console and points its output there. Output that the launcher already pointed at a
// file, a pipe or a console is left exactly as it is.
[SupportedOSPlatform("windows")]
internal static class ConsoleOutput
{
    private const int StandardOutput = -11;
    private const uint ParentProcess = 0xFFFFFFFF;
    private const uint Unknown = 0x0000;

    internal static void UseParentConsole()
    {
        try
        {
            // A handle that already names a file, pipe or console works without help, and attaching
            // would replace it - taking redirection away from whoever asked for it.
            var handle = GetStdHandle(StandardOutput);
            if (handle != 0 && handle != -1 && GetFileType(handle) != Unknown) return;
            // Nothing is listening, so either a terminal started this and its console can be joined,
            // or Explorer did and there is nothing to join.
            if (!AttachConsole(ParentProcess)) return;
            Rebind();
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            // Without these entry points the command line simply prints nowhere, which is cosmetic.
        }
    }

    // The standard writers were built when there was no console, so they have to be replaced with
    // ones over the handles the attach just supplied.
    private static void Rebind()
    {
        try
        {
            var output = Console.OpenStandardOutput();
            if (output != Stream.Null) Console.SetOut(new StreamWriter(output) { AutoFlush = true });
            var error = Console.OpenStandardError();
            if (error != Stream.Null) Console.SetError(new StreamWriter(error) { AutoFlush = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint process);
}
