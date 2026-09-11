using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shade;

public sealed record AutomationScreen(string Id, string Label, bool Enabled, bool UseGlobal, int Level,
    int X, int Y, int Width, int Height);
public sealed record AutomationState(int GlobalLevel, IReadOnlyList<AutomationScreen> Screens);
public sealed record AutomationPublication(string Topic, string Payload);
public sealed record AutomationCommand(string? ScreenId, string Control, string Value)
{
    public void Apply(ScreenControls controls)
    {
        switch (Control)
        {
            case "global": controls.SetGlobal(int.Parse(Value, CultureInfo.InvariantCulture)); break;
            case "level": controls.SetIndividual(ScreenId!, int.Parse(Value, CultureInfo.InvariantCulture)); break;
            case "enabled":
                if (controls.Get(ScreenId!).Enabled != (Value == "ON")) controls.Toggle(ScreenId!);
                break;
            case "global_link": controls.SetUseGlobal(ScreenId!, Value == "ON"); break;
        }
    }
}

// No hostnames, screen order or geometry in discovery identity. Dimming percentages are
// MQTT number entities, avoiding the inverted semantics of a light's brightness slider.
public sealed class HomeAssistantProtocol(string installationId)
{
    public string Root { get; } = "shade/" + ValidateId(installationId);
    public string Availability => Root + "/availability";
    private static string ValidateId(string id) => Guid.TryParseExact(id, "N", out _) ? id
        : throw new ArgumentException("Invalid installation identity.");
    private string Discovery(string component, string id) => $"homeassistant/{component}/{installationId}/{id}/config";
    public IEnumerable<AutomationPublication> Build(AutomationState state, ISet<string> knownScreens)
    {
        yield return Config("number", "global", "Global dimming", Root + "/global", null);
        yield return new(Root + "/global/state", state.GlobalLevel.ToString(CultureInfo.InvariantCulture));
        foreach (var screen in state.Screens)
        {
            knownScreens.Add(screen.Id);
            var topic = Root + "/screen/" + screen.Id;
            yield return Config("switch", screen.Id + "_enabled", screen.Label + " dimming", topic + "/enabled", topic);
            yield return Config("switch", screen.Id + "_global", screen.Label + " use global", topic + "/global_link", topic);
            yield return Config("number", screen.Id + "_level", screen.Label + " dimming level", topic + "/level", topic);
            yield return new(topic + "/availability", "online");
            yield return new(topic + "/enabled/state", screen.Enabled ? "ON" : "OFF");
            yield return new(topic + "/global_link/state", screen.UseGlobal ? "ON" : "OFF");
            yield return new(topic + "/level/state", screen.Level.ToString(CultureInfo.InvariantCulture));
            yield return new(topic + "/attributes", new JsonObject { ["X"] = screen.X, ["Y"] = screen.Y, ["Width"] = screen.Width, ["Height"] = screen.Height }.ToJsonString());
        }
        var connected = state.Screens.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in knownScreens.Where(id => !connected.Contains(id)))
            yield return new(Root + "/screen/" + id + "/availability", "offline");
    }

    private AutomationPublication Config(string component, string id, string name, string topic, string? screenTopic)
    {
        var availability = new JsonArray { (JsonNode)new JsonObject { ["topic"] = Availability } };
        if (screenTopic is not null) availability.Add((JsonNode)new JsonObject { ["topic"] = screenTopic + "/availability" });
        var fields = new JsonObject
        {
            ["name"] = name, ["unique_id"] = installationId + "_" + id,
            ["command_topic"] = topic + "/set", ["state_topic"] = topic + "/state",
            ["retain"] = false, ["qos"] = 1, ["optimistic"] = false,
            ["availability_mode"] = "all",
            ["availability"] = availability,
            ["device"] = new JsonObject { ["identifiers"] = new JsonArray("shade_" + installationId), ["name"] = "Shade", ["manufacturer"] = "Shade", ["model"] = "Screen dimmer" }
        };
        if (component == "number")
        { fields["min"] = 0; fields["max"] = DimLevel.Maximum; fields["step"] = 1; fields["unit_of_measurement"] = "%"; fields["mode"] = "slider"; }
        if (screenTopic is not null) fields["json_attributes_topic"] = screenTopic + "/attributes";
        return new(Discovery(component, id), fields.ToJsonString());
    }

    public IEnumerable<AutomationPublication> Remove(IEnumerable<string> knownScreens)
    {
        yield return new(Discovery("number", "global"), "");
        foreach (var id in knownScreens)
        {
            yield return new(Discovery("switch", id + "_enabled"), "");
            yield return new(Discovery("switch", id + "_global"), "");
            yield return new(Discovery("number", id + "_level"), "");
            yield return new(Root + "/screen/" + id + "/availability", "offline");
        }
    }

    public AutomationCommand? Parse(string topic, string payload, bool retained, AutomationState state)
    {
        if (retained || payload.Length > 16) return null;
        string? id = null; string control;
        if (topic == Root + "/global/set") control = "global";
        else
        {
            var prefix = Root + "/screen/";
            if (!topic.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var parts = topic[prefix.Length..].Split('/');
            if (parts.Length != 3 || parts[2] != "set" || !state.Screens.Any(s => s.Id == parts[0])) return null;
            id = parts[0]; control = parts[1];
        }
        if (control is "level" or "global")
        {
            // Accept equivalent MQTT numeric representations such as 20 and 20.0, but
            // reject fractions rather than silently rounding the requested one-percent scale.
            if (!decimal.TryParse(payload, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var level)
                || level is < 0 or > DimLevel.Maximum || decimal.Truncate(level) != level) return null;
            payload = ((int)level).ToString(CultureInfo.InvariantCulture);
        }
        else if (control is not ("enabled" or "global_link") || payload is not ("ON" or "OFF")) return null;
        return new(id, control, payload);
    }
}
