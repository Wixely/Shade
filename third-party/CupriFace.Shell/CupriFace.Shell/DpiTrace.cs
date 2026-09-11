using System.Diagnostics;

namespace CupriFace.Shell;

/// <summary>
/// What the window heard, and when, while its scale was changing.
///
/// <para>Exists for the same reason <c>KeyDiag</c> and <c>CUPRIFACE_RESIZE_DEBUG</c> do: monitor
/// transitions are a timing question that cannot be answered from a machine with one scale, and
/// "it detects it going one way but not the other" is not a claim any amount of reading the source
/// can settle. The window has to be able to say which callbacks it actually received.</para>
///
/// <para>Two outputs, because the two audiences differ. A bounded ring of SIGNIFICANT events is
/// always recorded and readable from inside the app (the DPI probe shows it), so a tester drags a
/// window and reads the answer off the screen. Verbose per-event tracing — including the callbacks
/// that changed nothing, which is exactly what you need when the complaint is "nothing happened" —
/// is behind <c>CUPRIFACE_DPI_DEBUG</c>: set it to <c>1</c> for stderr, or to a file path.</para>
/// </summary>
public static class DpiTrace
{
    private const int RingSize = 14;
    private static readonly Queue<string> Ring = new(RingSize);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string? Sink = Environment.GetEnvironmentVariable("CUPRIFACE_DPI_DEBUG");
    private static readonly bool Verbose = !string.IsNullOrEmpty(Sink);

    /// <summary>Callbacks seen since start, whether or not they changed anything. A monitor crossing
    /// that "does nothing" is a different bug depending on whether these are still climbing.</summary>
    public static int MoveEvents { get; private set; }

    /// <summary>Framebuffer-resize callbacks seen.</summary>
    public static int ResizeEvents { get; private set; }

    /// <summary>The most recent significant events, oldest first — scale changes and the resizes
    /// they caused. Bounded, so it costs nothing to leave on.</summary>
    public static IReadOnlyList<string> Recent
    {
        get { lock (Ring) return Ring.ToArray(); }
    }

    /// <summary>A callback fired. Counted always; written out only when verbose, because these
    /// arrive continuously during a drag and would swamp both the ring and the log.</summary>
    public static void Callback(string source, string detail)
    {
        if (source == "move") MoveEvents++;
        else if (source == "resize") ResizeEvents++;
        if (Verbose) Write($"{source} {detail}");
    }

    /// <summary>Something actually changed. Kept in the ring AND written out.</summary>
    public static void Significant(string line)
    {
        var stamped = $"{Clock.Elapsed.TotalSeconds,7:F2}s  {line}";
        lock (Ring)
        {
            if (Ring.Count == RingSize) Ring.Dequeue();
            Ring.Enqueue(stamped);
        }
        if (Verbose) Write(line);
    }

    private static void Write(string line)
    {
        var text = $"[dpi {Clock.Elapsed.TotalSeconds,7:F2}s] {line}";
        try
        {
            if (Sink is "1" or "true" or "TRUE") Console.Error.WriteLine(text);
            else if (Sink is { Length: > 0 }) File.AppendAllText(Sink, text + Environment.NewLine);
        }
        catch { /* diagnostics never throw */ }
    }
}
