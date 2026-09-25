using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Shade;

internal sealed record InstanceReply(bool Ok, string Message, IReadOnlyList<string> Lines);

// The running instance owns one control channel for its own account and desktop session.
//
// Access control: both ends pass PipeOptions.CurrentUserOnly. On Windows that restricts the pipe's
// security descriptor to the account that created it, and makes a client refuse a pipe owned by
// anyone else, so another account can neither read forwarded commands nor impersonate the instance.
// On Linux the backing socket is created owner-only and its ownership is verified before use.
// Nothing here binds a network address, so the channel is unreachable from other machines.
//
// Input handling: a request is ASCII, length-capped, read under a deadline, and parsed into the
// fixed ShadeOperation vocabulary before anything is applied. The channel never accepts a path, a
// credential or anything to execute, and it applies nothing itself - it hands parsed operations to
// the interface thread and waits for that thread's own result.
internal static class InstanceChannel
{
    internal const int Protocol = 1;
    internal const int MaximumOperations = 64;
    internal const int MaximumRequestBytes = 4096;
    // The most that is read from one connection before it is dropped, including data read only so a
    // refused sender can finish writing.
    private const int DrainLimit = 64 * 1024;
    private const string Terminator = "end";
    internal static readonly TimeSpan Connection = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan Apply = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan Close = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Ascii = new(encoderShouldEmitUTF8Identifier: false);

    // Windows keeps pipe names in one machine-wide namespace, so the name itself must separate
    // accounts and terminal-services sessions; on Linux the session mutex name already does.
    internal static string NameFor(string identity) =>
        "Shade." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];

    private static bool Allowed(byte value) => value is >= 0x20 and <= 0x7e or (byte)'\n' or (byte)'\r';

    private static string[] Split(ReadOnlySpan<byte> request) =>
        Ascii.GetString(request).Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // Read until the terminating line, refusing anything oversized, non-ASCII or too slow. Returns
    // null when the request is unusable, which the caller answers with an error and no state change.
    //
    // An unusable request is still read to its terminator rather than abandoned: a sender blocked
    // half-way through a write would never read the refusal, leaving both ends waiting until their
    // deadlines expired. Draining is itself bounded, so a sender that never terminates is dropped.
    internal static async Task<IReadOnlyList<string>?> ReadAsync(PipeStream pipe, CancellationToken token)
    {
        var buffer = new byte[DrainLimit];
        var length = 0;
        var usable = true;
        while (true)
        {
            if (length == buffer.Length) return null;
            var read = await pipe.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) return null;
            for (var index = length; index < length + read; index++) if (!Allowed(buffer[index])) usable = false;
            length += read;
            if (length > MaximumRequestBytes) usable = false;
            var lines = Split(buffer.AsSpan(0, length));
            if (lines.Length > MaximumOperations + 1) usable = false;
            if (!lines.Contains(Terminator)) continue;
            return usable ? lines.TakeWhile(line => line != Terminator).ToArray() : null;
        }
    }

    internal static async Task WriteAsync(PipeStream pipe, IEnumerable<string> lines, CancellationToken token)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line).Append('\n');
        text.Append(Terminator).Append('\n');
        await pipe.WriteAsync(Ascii.GetBytes(text.ToString()), token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    internal static Task GreetAsync(PipeStream pipe, int process, CancellationToken token) =>
        WriteAsync(pipe, [$"shade {Protocol.ToString(CultureInfo.InvariantCulture)} {process.ToString(CultureInfo.InvariantCulture)}"], token);

    internal static Task RejectAsync(PipeStream pipe, string message, CancellationToken token) =>
        WriteAsync(pipe, ["error " + Single(message)], token);

    internal static Task ReplyAsync(PipeStream pipe, InstanceResult result, CancellationToken token) =>
        result.Ok ? WriteAsync(pipe, result.Lines.Select(Single).Prepend("ok"), token)
            : RejectAsync(pipe, result.Message, token);

    // Screen labels and failure text reach this protocol from display hardware and exceptions, so
    // they are flattened to one printable ASCII line before they can break the line framing.
    internal static string Single(string value)
    {
        var text = new StringBuilder();
        foreach (var character in value.Length > 200 ? value[..200] : value)
            text.Append(character is >= ' ' and <= '~' ? character : ' ');
        return text.ToString().Trim().Length == 0 ? "unavailable" : text.ToString().Trim();
    }

    // Windows can discard a pipe's buffered bytes when the server disconnects first, which would lose
    // the reply the caller is waiting on - and, for a quit request, lose the only confirmation that
    // the instance is going away. Unix sockets close gracefully and need nothing here.
    //
    // The wait is bounded: a caller that asks for work and then never reads must not wedge the
    // channel for every later launch.
    internal static async Task DeliverAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !pipe.IsConnected) return;
        var drain = Task.Run(pipe.WaitForPipeDrain, CancellationToken.None);
        // Disconnecting makes an abandoned drain throw on its own thread; observe it so it stays quiet.
        _ = drain.ContinueWith(static completed => _ = completed.Exception, TaskScheduler.Default);
        // Best effort by nature: a caller that has already read and closed makes this throw, and that
        // is the successful case. Nothing here may disturb the reply that has already been written.
        try { await drain.WaitAsync(Close, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    internal static async Task<InstanceReply> SendAsync(string name, IReadOnlyList<ShadeOperation> operations,
        Action<int>? foundInstance = null, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Connection);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        var greeting = await ReadAsync(pipe, deadline.Token).ConfigureAwait(false);
        // "shade <protocol> <process>". An instance speaking a protocol this build does not know is
        // left alone rather than sent commands it might misread.
        var fields = greeting is { Count: 1 } ? greeting[0].Split(' ') : [];
        if (fields.Length != 3 || fields[0] != "shade" || fields[1] != Protocol.ToString(CultureInfo.InvariantCulture))
            throw new IOException("Unknown control-channel protocol.");
        if (int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var process))
            foundInstance?.Invoke(process);
        await WriteAsync(pipe, operations.Select(operation => operation.Write()), deadline.Token).ConfigureAwait(false);
        var reply = await ReadAsync(pipe, deadline.Token).ConfigureAwait(false)
            ?? throw new IOException("The running instance did not answer.");
        if (reply.Count > 0 && reply[0] == "ok") return new(true, "", reply.Skip(1).ToArray());
        var message = reply.Count > 0 && reply[0].StartsWith("error ", StringComparison.Ordinal)
            ? reply[0]["error ".Length..] : "The running instance rejected the request.";
        return new(false, message, []);
    }
}

internal sealed class InstanceServer : IDisposable
{
    private readonly string name;
    private readonly InstanceRequests requests;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;

    private InstanceServer(string name, NamedPipeServerStream pipe, InstanceRequests requests)
    {
        this.name = name;
        this.requests = requests;
        worker = Task.Run(() => ServeAsync(pipe));
    }

    // One instance of the name at a time: while this process holds it, no other program can create
    // the channel, so a launch either reaches this instance or reaches nothing.
    private static NamedPipeServerStream Create(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    // Returns null when the channel cannot be owned - most often because another program already
    // holds the name. Shade then runs normally without accepting forwarded commands rather than
    // trusting a channel it does not own. The first pipe is created here, not on the loop's thread,
    // so a taken name is reported to the caller instead of vanishing into a background retry.
    internal static InstanceServer? TryStart(string name, InstanceRequests requests)
    {
        try { return new(name, Create(name), requests); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or NotSupportedException)
        {
            return null;
        }
    }

    // Serves one launch at a time for the life of the process. The same server instance is reused and
    // merely disconnected between clients, never disposed and re-created: on Linux a named pipe is a
    // socket file that is unlinked when the stream closes, so re-creating per connection would leave a
    // window in which the channel does not exist and a second launch would find nothing there.
    private async Task ServeAsync(NamedPipeServerStream first)
    {
        var pipe = first;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                    await HandleAsync(pipe).ConfigureAwait(false);
                    if (pipe.IsConnected) pipe.Disconnect();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    // A client that dropped mid-exchange leaves this instance unusable, so replace it
                    // rather than surrendering the channel for the rest of the session.
                    pipe.Dispose();
                    try { await Task.Delay(250, cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    try { pipe = Create(name); }
                    catch (Exception retry) when (retry is IOException or UnauthorizedAccessException) { return; }
                }
            }
        }
        finally { pipe.Dispose(); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        deadline.CancelAfter(InstanceChannel.Connection);
        await InstanceChannel.GreetAsync(pipe, Environment.ProcessId, deadline.Token).ConfigureAwait(false);
        var result = await ReceiveAsync(pipe, deadline.Token).ConfigureAwait(false);
        await InstanceChannel.ReplyAsync(pipe, result, deadline.Token).ConfigureAwait(false);
        await InstanceChannel.DeliverAsync(pipe, deadline.Token).ConfigureAwait(false);
        // Only once the caller holds the reply, so a quit request never looks like a dropped channel.
        if (result.Shutdown) requests.ConfirmShutdown();
    }

    private async Task<InstanceResult> ReceiveAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        var lines = await InstanceChannel.ReadAsync(pipe, token).ConfigureAwait(false);
        if (lines is null) return InstanceResult.Rejected("The request was malformed or too large.");
        var operations = new List<ShadeOperation>(lines.Count);
        foreach (var line in lines)
        {
            // Nothing is applied when any line fails to parse, so a rejected request cannot leave
            // the interface half-changed.
            if (!ShadeOperation.TryRead(line, out var operation))
                return InstanceResult.Rejected("The request contained an unsupported control.");
            operations.Add(operation!);
        }
        var submitted = requests.Submit(operations);
        if (submitted is null) return InstanceResult.Rejected("Shade is busy and did not accept the request.");
        try { return await submitted.WaitAsync(InstanceChannel.Apply, token).ConfigureAwait(false); }
        catch (TimeoutException) { return InstanceResult.Rejected("Shade did not apply the request in time."); }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try { worker.Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException) { }
        cancellation.Dispose();
    }
}
