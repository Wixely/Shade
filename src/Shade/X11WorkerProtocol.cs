namespace Shade;

public sealed record X11Request(long Sequence, string Operation, string? Id = null, int Level = 0,
    ControlPreferences? Preferences = null, string? Name = null, string? ExistingId = null,
    Dictionary<string, int>? Levels = null);
public sealed record X11State(Display[] Displays, Dictionary<string, int> Levels, ControlPreferences Preferences,
    AssignmentChoice[] Assignments, long RestoreVersion, string Status, bool NeedsRecovery, bool NeedsBackup,
    bool RecoverySetupNeeded = false);
public sealed record X11Response(long Sequence, X11State State, string? Error = null);
