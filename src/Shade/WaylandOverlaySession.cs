namespace Shade;

// One worker actor owns all calls. ReadNext blocks until a native event; no recurring frame work.
// The caller supplies independent effective levels and must establish recovery before enabling them.
internal sealed class WaylandOverlaySession : IDisposable
{
    private readonly WaylandWire wire;
    private readonly WaylandOutputCatalog catalog;
    private readonly Dictionary<string, int> levels = [];
    private WaylandLayerSurfaces? surfaces;
    private readonly WaylandRecovery? recovery;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;
    private WaylandOverlaySession(WaylandWire wire, WaylandRecovery? recovery)
    { this.wire = wire; this.recovery = recovery; catalog = new(wire); }
    internal IReadOnlyList<Display> Displays => catalog.Displays;
    internal long Revision => catalog.Revision;
    internal static async Task<WaylandOverlaySession> Create(CancellationToken token, WaylandRecovery? recovery = null)
    {
        var session = new WaylandOverlaySession(await WaylandWire.Connect(token), recovery);
        try
        {
            await session.catalog.Initialize(token);
            // Check all required globals before allocating shared memory or creating surfaces.
            var compositor = await session.catalog.BindRequired("wl_compositor", 4, token);
            var layerShell = await session.catalog.BindRequired("zwlr_layer_shell_v1", 1, token);
            var viewporter = await session.catalog.BindRequired("wp_viewporter", 1, token);
            var shm = await session.catalog.BindRequired("wl_shm", 1, token);
            var palette = new WaylandShmPalette(session.wire, shm);
            await palette.Initialize(token);
            await session.catalog.Synchronize(token);
            session.surfaces = new(session.wire, compositor, layerShell, viewporter, palette.GetBuffer);
            if (recovery is not null) _ = session.WatchRecovery();
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    internal async Task ApplyLevels(IReadOnlyDictionary<string, int> changes, CancellationToken token)
    {
        using var budget = WriteBudget(token); token = budget.Token;
        using var interrupted = token.Register(Dispose);
        if (changes.Values.Any(level => level > 0) && recovery?.Ready != true)
            throw new InvalidOperationException("Wayland recovery shortcut is not registered.");
        foreach (var pair in changes) { DimLevel.Validate(pair.Value); _ = catalog.OutputObject(pair.Key); }
        foreach (var pair in changes) levels[pair.Key] = pair.Value;
        await Reconcile(token);
    }
    private async Task Reconcile(CancellationToken token)
    {
        var displays = catalog.Displays;
        var connected = displays.Select(d => d.Id).ToHashSet();
        // Connection IDs alone cannot authorize physical monitor recall. The worker's assignment
        // policy may restore intent explicitly after resolving a returning output.
        foreach (var id in levels.Keys.Where(id => !connected.Contains(id)).ToArray()) levels.Remove(id);
        await surfaces!.Reconcile(displays.Select(d => new WaylandOverlayTarget(d, catalog.OutputObject(d.Id), levels.GetValueOrDefault(d.Id))).ToArray(), token);
    }
    internal async Task ReadNext(CancellationToken token)
    {
        await Dispatch(await ReadMessage(token), token);
    }
    internal Task<WaylandMessage> ReadMessage(CancellationToken token) => wire.Read(token);
    internal async Task Dispatch(WaylandMessage message, CancellationToken token)
    {
        using var budget = WriteBudget(token); token = budget.Token;
        using var interrupted = token.Register(Dispose);
        var revision = catalog.Revision;
        await catalog.Handle(message, token);
        if (catalog.Revision != revision) await Reconcile(token);
        await surfaces!.Handle(message, token);
    }
    internal async Task Restore(CancellationToken token)
    {
        using var budget = WriteBudget(token); token = budget.Token;
        using var interrupted = token.Register(Dispose);
        levels.Clear(); await surfaces!.Restore(token);
    }
    private static CancellationTokenSource WriteBudget(CancellationToken token)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(2)); return budget;
    }
    private async Task WatchRecovery()
    {
        await Task.WhenAny(recovery!.Lost, stopped.Task);
        if (recovery.Lost.IsCompleted) Dispose();
    }
    // Disconnect destroys every server-owned surface and buffer, including after partial startup.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopped.TrySetResult(); catalog.Dispose();
    }
}
