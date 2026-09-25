using System.Threading.Channels;

namespace Shade;

public sealed record InstanceResult(bool Ok, string Message, IReadOnlyList<string> Lines, bool Shutdown)
{
    internal static InstanceResult Accepted(IReadOnlyList<string>? lines = null, bool shutdown = false) =>
        new(true, "", lines ?? [], shutdown);
    internal static InstanceResult Rejected(string message) => new(false, message, [], false);
}

public sealed class InstanceRequest(IReadOnlyList<ShadeOperation> operations)
{
    private readonly TaskCompletionSource<InstanceResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal IReadOnlyList<ShadeOperation> Operations { get; } = operations;
    internal Task<InstanceResult> Completion => completion.Task;
    internal void Complete(InstanceResult result) => completion.TrySetResult(result);
}

// Forwarded control requests cross from the channel's background task to the interface thread,
// which owns every control and overlay. The channel never touches that state itself.
public sealed class InstanceRequests
{
    private readonly Channel<InstanceRequest> pending =
        Channel.CreateBounded<InstanceRequest>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropWrite });
    private const int None = 0, Confirmed = 1, Claimed = 2;
    private int shutdown;

    internal Task<InstanceResult>? Submit(IReadOnlyList<ShadeOperation> operations)
    {
        var request = new InstanceRequest(operations);
        // A full queue means the interface thread is not draining. Refusing is better than
        // queueing work whose reply nobody is waiting for any more.
        return pending.Writer.TryWrite(request) ? request.Completion : null;
    }

    internal bool TryRead(out InstanceRequest? request) => pending.Reader.TryRead(out request);

    // Confirmed only after a quit request's reply reached the caller, so it learns the outcome before
    // this process stops. Claimed exactly once: the interface thread asks on every tick, and tearing
    // the application down more than once is not a thing that can be done twice.
    internal void ConfirmShutdown() => Interlocked.CompareExchange(ref shutdown, Confirmed, None);
    internal bool TryClaimShutdown() => Interlocked.CompareExchange(ref shutdown, Claimed, Confirmed) == Confirmed;

    internal void Enqueue(IReadOnlyList<ShadeOperation> operations)
    {
        if (operations.Count > 0) pending.Writer.TryWrite(new(operations));
    }
}
