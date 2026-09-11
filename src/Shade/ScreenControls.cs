namespace Shade;

// UI control intent is separate from effective overlay opacity: a linked screen at 0%
// may still be enabled, while a disabled screen must never be revived by global changes.
public sealed class ScreenControl
{
    public bool Enabled { get; internal set; }
    public bool UseGlobal { get; internal set; } = true;
    public int IndividualLevel { get; internal set; } = 30;
    internal int AppliedLevel { get; set; }
}

public sealed class ScreenControls
{
    private readonly IDimmingBackend backend;
    private readonly Dictionary<string, ScreenControl> screens = [];
    private long restoreVersion;
    public int GlobalLevel { get; private set; } = 30;
    public long Revision { get; private set; }

    public ScreenControls(IDimmingBackend backend)
    {
        this.backend = backend;
        restoreVersion = backend.RestoreVersion;
        var initial = backend.Displays.Select(d => backend.GetLevel(d.Id)).Where(l => l > 0).Distinct().ToArray();
        if (backend.Preferences is { } saved) GlobalLevel = saved.GlobalLevel;
        else if (initial.Length == 1) GlobalLevel = initial[0];
        Synchronize();
    }

    public ScreenControl Get(string id)
    {
        Synchronize();
        return screens.TryGetValue(id, out var state) ? state : throw new ArgumentException("Unknown screen.", nameof(id));
    }

    private void Synchronize()
    {
        if (restoreVersion != backend.RestoreVersion)
        {
            restoreVersion = backend.RestoreVersion;
            foreach (var state in screens.Values) { state.Enabled = false; state.AppliedLevel = 0; }
        }
        foreach (var display in backend.Displays)
        {
            var actual = backend.GetLevel(display.Id);
            var saved = display.CanRemember ? backend.Preferences?.Screens.GetValueOrDefault(display.Id) : null;
            if (!screens.TryGetValue(display.Id, out var state))
            {
                screens.Add(display.Id, new()
                {
                    Enabled = saved?.EffectiveLevel(GlobalLevel) == actual ? saved.Enabled : actual > 0, AppliedLevel = actual,
                    IndividualLevel = saved?.IndividualLevel ?? (actual > 0 ? actual : 30),
                    UseGlobal = saved?.UseGlobal ?? (actual == 0 || actual == GlobalLevel)
                });
            }
            else if (actual != state.AppliedLevel)
            {
                // External changes, including emergency restore, are authoritative.
                state.Enabled = saved?.EffectiveLevel(GlobalLevel) == actual ? saved.Enabled : actual > 0;
                state.AppliedLevel = actual;
                if (saved?.EffectiveLevel(GlobalLevel) == actual)
                { state.IndividualLevel = saved.IndividualLevel; state.UseGlobal = saved.UseGlobal; }
                else if (actual > 0) { state.IndividualLevel = actual; state.UseGlobal = actual == GlobalLevel; }
            }
        }
    }

    private void Apply(string id, ScreenControl state, int level)
    {
        backend.SetLevel(id, level);
        state.AppliedLevel = level;
    }

    private void Persist() => backend.SavePreferences(new(GlobalLevel, screens.ToDictionary(p => p.Key,
        p => new RememberedControl(p.Value.Enabled, p.Value.UseGlobal, p.Value.IndividualLevel))));

    public void SetGlobal(int level)
    {
        DimLevel.Validate(level);
        Synchronize();
        if (GlobalLevel == level) return;
        var changes = backend.Displays.Where(d => screens[d.Id].Enabled && screens[d.Id].UseGlobal)
            .ToDictionary(d => d.Id, _ => level);
        if (changes.Count > 0) backend.SetLevels(changes);
        GlobalLevel = level;
        foreach (var id in changes.Keys) screens[id].AppliedLevel = backend.GetLevel(id);
        Revision++;
        Persist();
    }

    public void Toggle(string id)
    {
        var state = Get(id);
        var enable = !state.Enabled;
        Apply(id, state, enable ? state.UseGlobal ? GlobalLevel : state.IndividualLevel : 0);
        state.Enabled = enable;
        Persist();
        Revision++;
    }

    public void SetUseGlobal(string id, bool useGlobal)
    {
        var state = Get(id);
        if (state.UseGlobal == useGlobal) return;
        // Unlinking freezes the current intensity. Linking an off screen never enables it.
        if (useGlobal && state.Enabled) Apply(id, state, GlobalLevel);
        if (!useGlobal && state.Enabled) state.IndividualLevel = backend.GetLevel(id);
        state.UseGlobal = useGlobal;
        Persist();
        Revision++;
    }

    public void SetIndividual(string id, int level)
    {
        DimLevel.Validate(level);
        var state = Get(id);
        Apply(id, state, level);
        state.IndividualLevel = level;
        state.UseGlobal = false;
        state.Enabled = level > 0;
        Persist();
        Revision++;
    }

    public void Disable(string id)
    {
        var state = Get(id);
        Apply(id, state, 0);
        state.Enabled = false;
        Persist();
        Revision++;
    }

    public void RestoreAll()
    {
        backend.RestoreAll();
        Synchronize();
        foreach (var state in screens.Values) state.Enabled = false;
        Persist();
        Revision++;
    }
}
