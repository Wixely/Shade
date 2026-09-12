using System.Text.Json;
using System.Threading.Channels;

namespace Shade;

// Private pipe contract shared with X11. All state mutations occur on this actor.
internal sealed class WaylandOverlayWorker
{
    private readonly Channel<X11Request> requests = Channel.CreateUnbounded<X11Request>(new() { SingleReader = true });
    private readonly SettingsStore? store;
    private readonly ShadeSettings settings;
    private readonly Dictionary<string, int> levels = [];
    private Display[] displays = [];
    private IReadOnlyList<Display> raw = Array.Empty<Display>();
    private WaylandOverlaySession? session;
    private WaylandRecovery? recovery;
    private Task<WaylandMessage>? nativeRead;
    private Task<WaylandRecovery>? setup;
    private CancellationTokenSource? setupTimeout;
    private Timer? saveTimer, retryTimer;
    private bool dirty, stopping;
    private int emergency, retrySeconds = 1;
    private long restoreVersion;
    private string status = "Starting Wayland overlays.";
    private WaylandOverlayWorker(string? path) { store = path is null ? null : new(path); settings = store?.Load() ?? new(); }
    internal static async Task<int> Run(string? path)
    {
        var worker = new WaylandOverlayWorker(path);
        try { await worker.Loop(); return 0; }
        catch { return 4; }
        finally { await worker.Close(); }
    }
    private void Queue(string operation) => requests.Writer.TryWrite(new(0, operation));
    private async Task Loop()
    {
        saveTimer = new(_ => Queue("save"), null, Timeout.Infinite, Timeout.Infinite);
        retryTimer = new(_ => Queue("connect"), null, Timeout.Infinite, Timeout.Infinite);
        _ = Task.Run(async () =>
        {
            try
            {
                while (await Console.In.ReadLineAsync() is { } line)
                {
                    if (line.Length > 2 * 1024 * 1024) break;
                    var request = JsonSerializer.Deserialize(line, ShadeJsonContext.Default.X11Request);
                    if (request is not null) await requests.Writer.WriteAsync(request);
                }
            }
            catch { }
            finally { Queue("quit"); }
        });
        await Connect(); Publish(0);
        var command = requests.Reader.ReadAsync().AsTask();
        while (!stopping)
        {
            var work = new List<Task> { command };
            if (nativeRead is not null) work.Add(nativeRead);
            if (setup is not null) work.Add(setup);
            await Task.WhenAny(work);
            if (Interlocked.Exchange(ref emergency, 0) != 0) { await Restore(); Publish(0); }
            if (setup?.IsCompleted == true)
            {
                try { recovery?.Dispose(); recovery = await setup; await Connect(); }
                catch { status = "Recovery setup was cancelled, denied or unavailable. Use Set up recovery shortcut to retry."; }
                finally { setup = null; setupTimeout?.Dispose(); setupTimeout = null; }
                Publish(0);
            }
            if (nativeRead?.IsCompleted == true)
            {
                try
                {
                    var revision = session!.Revision;
                    await session.Dispatch(await nativeRead, CancellationToken.None);
                    nativeRead = session.ReadMessage(CancellationToken.None);
                    if (session.Revision != revision) { await Reconcile(); Publish(0); }
                }
                catch { Unavailable("Wayland connection or recovery was lost. Shading is paused; reconnecting automatically."); Publish(0); }
            }
            if (command.IsCompleted)
            {
                var request = await command; string? error = null;
                try { await Execute(request); }
                catch (AssignmentException ex) { error = ex.Message; }
                catch { error = "The Wayland action could not be completed. Check recovery setup and connected screens."; }
                if (request.Operation != "save" || store?.Error is not null) Publish(request.Sequence, error);
                command = requests.Reader.ReadAsync().AsTask();
            }
        }
    }
    private async Task Connect()
    {
        DropSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            session = await WaylandOverlaySession.Create(timeout.Token, recovery?.Ready == true ? recovery : null);
            await Reconcile(); nativeRead = session.ReadMessage(CancellationToken.None);
            retrySeconds = 1; retryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            status = recovery?.Ready == true ? "Wayland overlays active. Recovery: " + recovery.TriggerDescription
                : "Set up a recovery shortcut before enabling Wayland dimming.";
        }
        catch (NotSupportedException ex) { Unavailable(ex.Message + " Shading is unavailable in this compositor; retrying automatically."); }
        catch { Unavailable("Wayland is unavailable; shading is paused and reconnecting automatically."); }
    }
    private void Unavailable(string message)
    {
        DropSession(); status = message;
        retryTimer?.Change(TimeSpan.FromSeconds(retrySeconds), Timeout.InfiniteTimeSpan); retrySeconds = Math.Min(30, retrySeconds * 2);
    }
    private void DropSession()
    {
        session?.Dispose(); session = null;
        if (nativeRead is not null) _ = Observe(nativeRead);
        nativeRead = null; levels.Clear(); displays = []; raw = Array.Empty<Display>();
    }
    private static async Task Observe(Task task) { try { await task; } catch { } }
    private async Task Reconcile()
    {
        raw = session!.Displays;
        var next = raw.Select(d => IdentityAssignments.Resolve(d, settings.Assignments)).ToArray();
        var desired = new Dictionary<string, int>();
        foreach (var d in next)
        {
            var level = levels.GetValueOrDefault(d.Id);
            if (!levels.ContainsKey(d.Id) && d.CanRemember)
                level = settings.Controls.TryGetValue(d.Id, out var control) ? control.EffectiveLevel(settings.GlobalLevel)
                    : settings.Displays.GetValueOrDefault(d.Id)?.Level ?? 0;
            desired[d.Id] = recovery?.Ready == true ? level : 0;
        }
        var native = raw.Select((d, index) => (d.Id, Level: desired[next[index].Id])).ToDictionary(p => p.Id, p => p.Level);
        await session.ApplyLevels(native, CancellationToken.None);
        displays = next; levels.Clear(); foreach (var pair in desired) levels.Add(pair.Key, pair.Value);
        if (recovery?.Ready == true) foreach (var d in displays.Where(d => d.CanRemember)) Remember(d);
    }
    private async Task Execute(X11Request request)
    {
        switch (request.Operation)
        {
            case "set": await Set(new Dictionary<string, int> { [request.Id ?? ""] = request.Level }); break;
            case "set-many": await Set(request.Levels ?? throw new ArgumentException()); break;
            case "preferences":
                var preferences = request.Preferences ?? throw new ArgumentException();
                DimLevel.Validate(preferences.GlobalLevel);
                foreach (var control in preferences.Screens.Values) DimLevel.Validate(control.IndividualLevel);
                settings.GlobalLevel = preferences.GlobalLevel;
                foreach (var d in displays.Where(d => d.CanRemember))
                    if (preferences.Screens.TryGetValue(d.Id, out var control)) settings.Controls[d.Id] = control;
                settings.SetFullShade(preferences.AllowFullShade);
                ScheduleSave(); break;
            case "restore": await Restore(); break;
            case "assign":
                IdentityAssignments.Assign(settings, raw, request.Id ?? "", request.Name ?? "", request.ExistingId);
                levels.Remove(request.Id ?? ""); ScheduleSave(); await Reconcile(); break;
            case "detach": IdentityAssignments.Detach(settings, request.Id ?? ""); levels.Remove(request.Id ?? ""); ScheduleSave(); await Reconcile(); break;
            case "configure-recovery":
                if (session is null || setup is not null || recovery?.Ready == true) break;
                setupTimeout = new(TimeSpan.FromMinutes(2)); status = "Choose and confirm Shade's recovery shortcut in the desktop dialog.";
                setup = WaylandRecovery.Create(() => { Interlocked.Exchange(ref emergency, 1); Queue("wake"); }, setupTimeout.Token,
                    changed: () => Queue("recovery-status")); break;
            case "recovery-status":
                if (recovery?.Ready == true) status = "Wayland overlays active. Recovery: " + recovery.TriggerDescription;
                break;
            case "connect": case "refresh": await Connect(); break;
            case "save": Save(); break;
            case "recover": store?.Recover(settings); break;
            case "wake": break;
            case "quit": stopping = true; break;
            default: throw new ArgumentException();
        }
    }
    private async Task Set(IReadOnlyDictionary<string, int> changes)
    {
        if (session is null) throw new InvalidOperationException();
        var native = new Dictionary<string, int>();
        foreach (var pair in changes)
        {
            DimLevel.Validate(pair.Value);
            var index = Array.FindIndex(displays, d => d.Id == pair.Key);
            if (index < 0) throw new ArgumentException();
            native.Add(raw[index].Id, pair.Value);
        }
        await session.ApplyLevels(native, CancellationToken.None);
        foreach (var pair in changes) { levels[pair.Key] = pair.Value; Remember(displays.Single(d => d.Id == pair.Key)); }
    }
    private async Task Restore()
    {
        try { if (session is not null) await session.Restore(CancellationToken.None); }
        finally
        {
            foreach (var id in levels.Keys.ToArray()) levels[id] = 0;
            foreach (var id in settings.Controls.Keys.ToArray()) settings.Controls[id] = settings.Controls[id] with { Enabled = false };
            foreach (var id in settings.Displays.Keys.ToArray()) settings.Displays[id] = settings.Displays[id] with { Level = 0 };
            restoreVersion++; ScheduleSave();
        }
    }
    private void Remember(Display d)
    {
        if (!d.CanRemember) return;
        settings.Displays[d.Id] = new(levels.GetValueOrDefault(d.Id), d.X, d.Y, d.Width, d.Height); ScheduleSave();
    }
    private void ScheduleSave() { dirty = true; if (store is not null) saveTimer?.Change(750, Timeout.Infinite); }
    private void Save() { if (dirty && store is not null) { store.Save(settings); dirty = false; } }
    private void Publish(long sequence, string? error = null)
    {
        var state = new X11State(displays, new(levels), new(settings.GlobalLevel, new Dictionary<string, RememberedControl>(settings.Controls), settings.AllowFullShade),
            settings.Assignments.Select(p => new AssignmentChoice(p.Key, p.Value.Name, raw.Any(d => d.Id == p.Value.ConnectionId))).ToArray(),
            restoreVersion, store?.Error ?? status, store?.Error is not null, store?.PreservingUnreadableFile == true,
            session is not null && recovery?.Ready != true && setup is null);
        Console.WriteLine(JsonSerializer.Serialize(new X11Response(sequence, state, error), ShadeJsonContext.Default.X11Response)); Console.Out.Flush();
    }
    private async Task Close()
    {
        saveTimer?.Dispose(); retryTimer?.Dispose(); setupTimeout?.Cancel();
        if (setup is not null) { try { (await setup).Dispose(); } catch { } }
        DropSession(); recovery?.Dispose(); setupTimeout?.Dispose(); Save();
    }
}
