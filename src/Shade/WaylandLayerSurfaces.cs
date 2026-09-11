using System.Text;

namespace Shade;

internal sealed record WaylandOverlayTarget(Display Display, uint Output, int Level);

// Actor-owned layer-shell v1 / compositor v4 / viewporter v1 surface lifecycle.
// No input objects, frame callbacks or mutable buffer reuse. Recovery belongs to the worker.
internal sealed class WaylandLayerSurfaces(WaylandWire wire, uint compositor, uint layerShell, uint viewporter,
    Func<int, CancellationToken, Task<uint>> getBuffer)
{
    private sealed class Overlay(WaylandOverlayTarget target, uint surface, uint layer, uint viewport)
    {
        internal WaylandOverlayTarget Target = target;
        internal readonly uint Surface = surface, Layer = layer, Viewport = viewport;
        internal int Width, Height, AppliedLevel;
        internal bool Configured;
    }
    private readonly Dictionary<string, Overlay> overlays = [];
    internal int Count => overlays.Count;
    internal async Task Reconcile(IReadOnlyList<WaylandOverlayTarget> targets, CancellationToken token)
    {
        // Validate the complete batch before creating/removing any visible surface.
        var desired = new Dictionary<string, WaylandOverlayTarget>();
        foreach (var target in targets)
        {
            DimLevel.Validate(target.Level);
            if (target.Output == 0 || target.Display.Width <= 0 || target.Display.Height <= 0 || !desired.TryAdd(target.Display.Id, target))
                throw new ArgumentException("Invalid Wayland overlay targets.");
        }
        foreach (var item in overlays.ToArray())
            if (!desired.TryGetValue(item.Key, out var target) || target.Level == 0 || target.Output != item.Value.Target.Output)
            {
                await Destroy(item.Value, token); overlays.Remove(item.Key);
            }
        foreach (var target in targets)
        {
            if (target.Level == 0) continue;
            if (!overlays.TryGetValue(target.Display.Id, out var overlay))
            {
                overlay = new(target, wire.Allocate(), wire.Allocate(), wire.Allocate());
                overlays.Add(target.Display.Id, overlay);
                await Create(overlay, token);
            }
            else
            {
                overlay.Target = target;
                if (overlay.Configured && overlay.AppliedLevel != target.Level) await Commit(overlay, token);
            }
        }
    }
    private async Task Create(Overlay overlay, CancellationToken token)
    {
        await wire.Send(compositor, 0, WaylandWire.Words(overlay.Surface), token);
        // Explicit output and highest layer; never ask the compositor to guess a monitor.
        var name = Encoding.UTF8.GetBytes("shade\0");
        var arguments = new byte[28];
        WaylandWire.Words(overlay.Layer, overlay.Surface, overlay.Target.Output, 3, (uint)name.Length).CopyTo(arguments, 0);
        name.CopyTo(arguments, 20);
        await wire.Send(layerShell, 0, arguments, token);
        await wire.Send(viewporter, 1, WaylandWire.Words(overlay.Viewport, overlay.Surface), token);
        var empty = wire.Allocate();
        await wire.Send(compositor, 1, WaylandWire.Words(empty), token);
        await wire.Send(overlay.Surface, 5, WaylandWire.Words(empty), token); // empty input region
        await wire.Send(empty, 0, [], token);
        await wire.Send(overlay.Layer, 0, WaylandWire.Words(0, 0), token); // compositor chooses full output size
        await wire.Send(overlay.Layer, 1, WaylandWire.Words(15), token); // all four anchors
        await wire.Send(overlay.Layer, 2, WaylandWire.Words(uint.MaxValue), token); // ignore panels' exclusive zones
        await wire.Send(overlay.Layer, 4, WaylandWire.Words(0), token); // never keyboard focus
        await wire.Send(overlay.Surface, 6, [], token); // initial bufferless commit; wait for configure
    }
    internal async Task<bool> Handle(WaylandMessage message, CancellationToken token)
    {
        var overlay = overlays.Values.FirstOrDefault(o => o.Layer == message.Target);
        if (overlay is null) return false;
        if (message.Opcode == 1)
        {
            new WaylandReader(message.Payload).End();
            // The worker must reconnect/reconcile after a compositor closes an output surface.
            // Never leave the remaining screens dimmed after losing part of the surface set.
            await Restore(token);
            throw new IOException("Wayland compositor closed an overlay surface.");
        }
        if (message.Opcode != 0) throw new InvalidDataException("Unknown Wayland layer event.");
        var reader = new WaylandReader(message.Payload);
        var serial = reader.UInt(); var width = reader.UInt(); var height = reader.UInt(); reader.End();
        if (width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("Invalid Wayland surface size.");
        overlay.Width = width == 0 ? overlay.Target.Display.Width : (int)width;
        overlay.Height = height == 0 ? overlay.Target.Display.Height : (int)height;
        await wire.Send(overlay.Layer, 6, WaylandWire.Words(serial), token);
        overlay.Configured = true;
        await Commit(overlay, token);
        return true;
    }
    private async Task Commit(Overlay overlay, CancellationToken token)
    {
        var buffer = await getBuffer(overlay.Target.Level, token);
        await wire.Send(overlay.Viewport, 2, WaylandWire.Words((uint)overlay.Width, (uint)overlay.Height), token);
        await wire.Send(overlay.Surface, 1, WaylandWire.Words(buffer, 0, 0), token);
        await wire.Send(overlay.Surface, 9, WaylandWire.Words(0, 0, 1, 1), token); // damage_buffer v4: one changed pixel
        await wire.Send(overlay.Surface, 6, [], token);
        overlay.AppliedLevel = overlay.Target.Level;
    }
    private async Task Destroy(Overlay overlay, CancellationToken token)
    {
        await wire.Send(overlay.Viewport, 0, [], token);
        await wire.Send(overlay.Layer, 7, [], token);
        await wire.Send(overlay.Surface, 0, [], token);
    }
    internal async Task Restore(CancellationToken token)
    {
        foreach (var overlay in overlays.Values) await Destroy(overlay, token);
        overlays.Clear();
    }
}
