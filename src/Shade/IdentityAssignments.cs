namespace Shade;

public sealed record ScreenAssignment(string Name, string ConnectionId);
public sealed record AssignmentChoice(string Id, string Name, bool Connected);
public sealed class AssignmentException(string message) : ArgumentException(message);

public static class IdentityAssignments
{
    public static Display Resolve(Display display, IReadOnlyDictionary<string, ScreenAssignment> assignments)
    {
        if (display.CanRemember) return display;
        var match = assignments.FirstOrDefault(p => p.Value.ConnectionId == display.Id);
        return match.Value is null ? display : display with
        {
            Id = match.Key, Label = match.Value.Name, CanRemember = true,
            IdentityNote = "Assigned to this connection. Reassign through Shade if this monitor moves."
        };
    }

    public static string Assign(ShadeSettings settings, IReadOnlyList<Display> connected, string connectionId, string name, string? existingId)
    {
        var display = connected.SingleOrDefault(d => d.Id == connectionId)
            ?? throw new AssignmentException("This screen disconnected. Select a connected screen again.");
        if (display.CanRemember) throw new AssignmentException("This screen already has a hardware identity.");
        name = name.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new AssignmentException("Enter a screen name between 1 and 64 characters.");
        if (settings.Assignments.Any(p => p.Key != existingId && string.Equals(p.Value.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new AssignmentException("That name already exists. Reuse its disconnected assignment or choose another name.");
        if (settings.Assignments.Any(p => p.Key != existingId && p.Value.ConnectionId == connectionId))
            throw new AssignmentException("Detach this connection's current assignment first.");
        var id = existingId ?? "assigned-v1-" + Guid.NewGuid().ToString("N");
        if (existingId is not null)
        {
            if (!settings.Assignments.TryGetValue(id, out var old)) throw new AssignmentException("That assignment no longer exists.");
            if (old.ConnectionId != connectionId && connected.Any(d => d.Id == old.ConnectionId))
                throw new AssignmentException("That assignment is still connected. Disconnect or detach it before reusing it.");
        }
        settings.Assignments[id] = new(name, connectionId);
        // Explicit enrollment never darkens a screen as a side effect, even when reusing a slot.
        settings.Controls[id] = settings.Controls.GetValueOrDefault(id, new(false, true, 30)) with { Enabled = false };
        settings.Displays[id] = new(0, display.X, display.Y, display.Width, display.Height);
        return id;
    }

    public static void Detach(ShadeSettings settings, string id)
    {
        if (!settings.Assignments.TryGetValue(id, out var assignment)) throw new AssignmentException("That assignment no longer exists.");
        settings.Assignments[id] = assignment with { ConnectionId = "" };
        if (settings.Controls.TryGetValue(id, out var control)) settings.Controls[id] = control with { Enabled = false };
        if (settings.Displays.TryGetValue(id, out var display)) settings.Displays[id] = display with { Level = 0 };
    }

    public static bool Valid(IReadOnlyDictionary<string, ScreenAssignment> assignments)
        => assignments.All(p => p.Key.StartsWith("assigned-v1-", StringComparison.Ordinal)
            && Guid.TryParseExact(p.Key[12..], "N", out _) && p.Value is not null
            && p.Value.Name is { Length: > 0 and <= 64 } && !p.Value.Name.Any(char.IsControl)
            && p.Value.ConnectionId is not null)
        && assignments.Values.Where(v => v.ConnectionId.Length > 0).Select(v => v.ConnectionId).Distinct().Count()
            == assignments.Values.Count(v => v.ConnectionId.Length > 0);
}
