using System.Security.Cryptography;
using System.Text;

namespace Shade;

internal sealed record WaylandOutputInfo(string Name = "", string Description = "", int X = 0, int Y = 0,
    int Width = 0, int Height = 0, int PixelWidth = 0, int PixelHeight = 0, int Scale = 1, int Transform = 0);

// A pending batch cannot alter the published rectangle before the protocol's done event.
internal sealed class WaylandOutputState(uint version, uint logicalVersion)
{
    private WaylandOutputInfo pending = new();
    private bool hasPosition, hasSize;
    internal WaylandOutputInfo? Current { get; private set; }
    internal bool Apply(bool logical, ushort opcode, byte[] payload)
    {
        var reader = new WaylandReader(payload); var commit = false;
        if (logical)
        {
            switch (opcode)
            {
                case 0: pending = pending with { X = reader.Int(), Y = reader.Int() }; hasPosition = true; break;
                case 1: pending = pending with { Width = reader.Int(), Height = reader.Int() }; hasSize = true; break;
                case 2: commit = logicalVersion < 3; break;
                case 3: pending = pending with { Name = reader.Text() }; break;
                case 4: pending = pending with { Description = reader.Text() }; break;
                default: throw new InvalidDataException("Unknown xdg-output event.");
            }
        }
        else
        {
            switch (opcode)
            {
                case 0:
                    _ = reader.Int(); _ = reader.Int(); _ = reader.Int(); _ = reader.Int(); _ = reader.Int();
                    _ = reader.Text(); _ = reader.Text(); pending = pending with { Transform = reader.Int() }; break;
                case 1:
                    var flags = reader.UInt(); var width = reader.Int(); var height = reader.Int(); _ = reader.Int();
                    if ((flags & 1) != 0) pending = pending with { PixelWidth = width, PixelHeight = height }; break;
                case 2: commit = logicalVersion >= 3; break;
                case 3: pending = pending with { Scale = reader.Int() }; break;
                case 4 when version >= 4: pending = pending with { Name = reader.Text() }; break;
                case 5 when version >= 4: pending = pending with { Description = reader.Text() }; break;
                default: throw new InvalidDataException("Unknown wl_output event.");
            }
        }
        reader.End();
        if (!commit || !hasPosition || !hasSize) return false;
        if (pending.Width <= 0 || pending.Height <= 0 || pending.Scale <= 0 || pending.Transform is < 0 or > 7)
            throw new InvalidDataException("Incomplete or invalid logical output geometry.");
        var changed = Current != pending; Current = pending; return changed;
    }
}

// Long-lived native catalog for the future Wayland worker. No surfaces or input objects.
// xdg-output is required: deriving layout from pixel mode/integer scale loses fractional scaling.
internal sealed class WaylandOutputCatalog : IDisposable
{
    private sealed record Global(string Interface, uint Version);
    private sealed record Output(uint Object, uint LogicalObject, uint Version, WaylandOutputState State);
    private readonly WaylandWire wire;
    private readonly Dictionary<uint, Global> globals = [];
    private readonly Dictionary<uint, Output> outputs = [];
    private readonly HashSet<uint> dependencies = [];
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly uint registry;
    private uint manager, managerName, managerVersion;
    private bool initialized;
    private long snapshotRevision = -1;
    private IReadOnlyList<Display> snapshot = Array.Empty<Display>();
    private readonly Dictionary<string, uint> snapshotOutputs = [];
    internal long Revision { get; private set; }
    internal WaylandOutputCatalog(WaylandWire wire) { this.wire = wire; registry = wire.Allocate(); }
    internal async Task Initialize(CancellationToken token)
    {
        await wire.Send(1, 1, WaylandWire.Words(registry), token);
        await wire.RoundTrip(Handle, token);
        var extension = globals.FirstOrDefault(g => g.Value.Interface == "zxdg_output_manager_v1" && g.Value.Version >= 2);
        if (extension.Value is null) throw new NotSupportedException("Wayland logical output descriptions v2 or newer are required.");
        managerName = extension.Key; managerVersion = Math.Min(extension.Value.Version, 3); manager = wire.Allocate();
        await wire.Send(registry, 0, WaylandWire.Binding(extension.Key, extension.Value.Interface, managerVersion, manager), token);
        foreach (var item in globals.Where(g => g.Value.Interface == "wl_output").ToArray()) await AddOutput(item.Key, item.Value, token);
        initialized = true;
        await wire.RoundTrip(Handle, token);
    }
    internal async Task ReadNext(CancellationToken token) => await Handle(await wire.Read(token), token);
    internal async Task<uint> BindRequired(string protocol, uint version, CancellationToken token)
    {
        var global = globals.FirstOrDefault(g => g.Value.Interface == protocol && g.Value.Version >= version);
        if (global.Value is null) throw new NotSupportedException($"Wayland requires {protocol} v{version}.");
        var id = wire.Allocate();
        await wire.Send(registry, 0, WaylandWire.Binding(global.Key, protocol, version, id), token);
        dependencies.Add(global.Key); return id;
    }
    internal Task Synchronize(CancellationToken token) => wire.RoundTrip(Handle, token);
    internal uint OutputObject(string id)
    {
        _ = Displays;
        return snapshotOutputs.TryGetValue(id, out var output) ? output : throw new ArgumentException("Wayland output is no longer connected.");
    }
    internal IReadOnlyList<Display> Displays
    {
        get
        {
            if (snapshotRevision != Revision) RefreshSnapshot();
            return snapshot;
        }
    }
    private void RefreshSnapshot()
    {
        var complete = outputs.Where(o => o.Value.State.Current is not null).ToArray();
        var names = complete.GroupBy(o => o.Value.State.Current!.Name).ToDictionary(g => g.Key, g => g.Count());
        snapshotOutputs.Clear();
        var displays = complete.Select(o =>
        {
            var value = o.Value.State.Current!;
            var stable = value.Name.Length > 0 && names[value.Name] == 1;
            var connection = stable ? value.Name : session + "|" + o.Key;
            var id = "wayland-connection-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection))).ToLowerInvariant();
            snapshotOutputs.Add(id, o.Value.Object);
            return new Display(id, value.Description.Length > 0 ? value.Description : value.Name.Length > 0 ? value.Name : "Wayland screen",
                value.X, value.Y, value.Width, value.Height, false,
                stable ? "Wayland connection name; hardware identity is unavailable. Assignment is required for recall."
                    : "Wayland output has no unique connection name; session identity only.");
        }).OrderBy(d => d.Y).ThenBy(d => d.X).ThenBy(d => d.Id).ToArray();
        snapshot = Array.AsReadOnly(displays);
        snapshotRevision = Revision;
    }
    private async Task AddOutput(uint name, Global value, CancellationToken token)
    {
        if (outputs.ContainsKey(name)) return;
        if (outputs.Count >= 128 || value.Version < 2) throw new NotSupportedException("Unsupported Wayland output catalog.");
        var version = Math.Min(value.Version, 4); var id = wire.Allocate(); var logical = wire.Allocate();
        outputs[name] = new(id, logical, version, new(version, managerVersion));
        await wire.Send(registry, 0, WaylandWire.Binding(name, "wl_output", version, id), token);
        await wire.Send(manager, 1, WaylandWire.Words(logical, id), token);
    }
    internal async Task Handle(WaylandMessage message, CancellationToken token)
    {
        if (message.Target == 1) return; // The transport processes delete_id acknowledgments.
        if (message.Target == registry)
        {
            var reader = new WaylandReader(message.Payload); var name = reader.UInt();
            if (message.Opcode == 0)
            {
                var protocol = reader.Text(); var version = reader.UInt(); reader.End();
                if (globals.Count >= 4096) throw new InvalidDataException("Wayland registry limit exceeded.");
                globals[name] = new(protocol, version);
                if (initialized && protocol == "wl_output") await AddOutput(name, globals[name], token);
            }
            else if (message.Opcode == 1)
            {
                reader.End(); globals.Remove(name);
                if (name == managerName || dependencies.Contains(name)) throw new IOException("A required Wayland global disappeared.");
                if (outputs.Remove(name, out var output))
                {
                    Revision++;
                    await wire.Send(output.LogicalObject, 0, [], token);
                    if (output.Version >= 3) await wire.Send(output.Object, 0, [], token);
                }
            }
            else throw new InvalidDataException("Unknown Wayland registry event.");
            return;
        }
        foreach (var output in outputs.Values)
            if (message.Target == output.Object || message.Target == output.LogicalObject)
            {
                if (output.State.Apply(message.Target == output.LogicalObject, message.Opcode, message.Payload)) Revision++;
                return;
            }
        // Events queued before a global removal may arrive for its now-retired objects.
    }
    public void Dispose() => wire.Dispose();
}
