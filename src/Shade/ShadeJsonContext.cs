using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shade;

[JsonSerializable(typeof(ShadeSettings))]
[JsonSerializable(typeof(AutomationSettings))]
[JsonSerializable(typeof(X11Request))]
[JsonSerializable(typeof(X11Response))]
internal partial class ShadeJsonContext : JsonSerializerContext
{
    internal static ShadeJsonContext Indented { get; } = new(new JsonSerializerOptions { WriteIndented = true });
}
