using System.Globalization;
using System.Text;

namespace Shade;

// Every control a command line may change in a running instance. Startup-only options
// (settings directory, ephemeral mode, tray behavior) are deliberately absent: a running
// instance cannot adopt them, so forwarding them would silently do nothing.
public enum ShadeVerb
{
    Global, Level, Enabled, Link, FullShade, Restore, HomeAssistant, Advanced, Activate, Hide, Status, Exit
}

// Screen 0 addresses every connected screen; otherwise it is the 1-based number shown in the
// interface. Value carries a percentage, or 1/0 for a switch.
public sealed record ShadeOperation(ShadeVerb Verb, int Screen = 0, int Value = 0)
{
    internal const int AllScreens = 0;
    internal const int HighestScreen = 64;

    private static string Switch(int value) => value != 0 ? "on" : "off";
    private static string Target(int screen) => screen == AllScreens
        ? "all" : screen.ToString(CultureInfo.InvariantCulture);

    // The wire form is the only representation the running instance parses, so it stays a
    // fixed number of ASCII tokens per verb with no free text.
    internal string Write() => Verb switch
    {
        ShadeVerb.Global => "global " + Value.ToString(CultureInfo.InvariantCulture),
        ShadeVerb.Level => "level " + Target(Screen) + " " + Value.ToString(CultureInfo.InvariantCulture),
        ShadeVerb.Enabled => "enabled " + Target(Screen) + " " + Switch(Value),
        ShadeVerb.Link => "link " + Target(Screen) + " " + Switch(Value),
        ShadeVerb.FullShade => "full-shade " + Switch(Value),
        ShadeVerb.HomeAssistant => "home-assistant " + Switch(Value),
        ShadeVerb.Advanced => "advanced " + Switch(Value),
        ShadeVerb.Restore => "restore",
        ShadeVerb.Activate => "activate",
        ShadeVerb.Hide => "hide",
        ShadeVerb.Status => "status",
        ShadeVerb.Exit => "exit",
        _ => throw new InvalidOperationException("Unknown control.")
    };

    internal static bool TryRead(string line, out ShadeOperation? operation)
    {
        operation = null;
        // Split on single spaces only: padding, tabs and empty tokens are malformed, not tolerated.
        var tokens = line.Split(' ');
        switch (tokens[0])
        {
            case "global" when tokens.Length == 2 && TryLevel(tokens[1], out var global):
                operation = new(ShadeVerb.Global, AllScreens, global); return true;
            case "level" when tokens.Length == 3 && TryTarget(tokens[1], out var levelScreen) && TryLevel(tokens[2], out var level):
                operation = new(ShadeVerb.Level, levelScreen, level); return true;
            case "enabled" when tokens.Length == 3 && TryTarget(tokens[1], out var enabledScreen) && TrySwitch(tokens[2], out var enabled):
                operation = new(ShadeVerb.Enabled, enabledScreen, enabled); return true;
            case "link" when tokens.Length == 3 && TryTarget(tokens[1], out var linkScreen) && TrySwitch(tokens[2], out var link):
                operation = new(ShadeVerb.Link, linkScreen, link); return true;
            case "full-shade" when tokens.Length == 2 && TrySwitch(tokens[1], out var fullShade):
                operation = new(ShadeVerb.FullShade, AllScreens, fullShade); return true;
            case "home-assistant" when tokens.Length == 2 && TrySwitch(tokens[1], out var automation):
                operation = new(ShadeVerb.HomeAssistant, AllScreens, automation); return true;
            case "advanced" when tokens.Length == 2 && TrySwitch(tokens[1], out var advanced):
                operation = new(ShadeVerb.Advanced, AllScreens, advanced); return true;
            case "restore" when tokens.Length == 1: operation = new(ShadeVerb.Restore); return true;
            case "activate" when tokens.Length == 1: operation = new(ShadeVerb.Activate); return true;
            case "hide" when tokens.Length == 1: operation = new(ShadeVerb.Hide); return true;
            case "status" when tokens.Length == 1: operation = new(ShadeVerb.Status); return true;
            case "exit" when tokens.Length == 1: operation = new(ShadeVerb.Exit); return true;
            default: return false;
        }
    }

    private static bool TryLevel(string token, out int value) =>
        int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= DimLevel.Maximum;

    private static bool TrySwitch(string token, out int value)
    {
        value = token == "on" ? 1 : 0;
        return token is "on" or "off";
    }

    private static bool TryTarget(string token, out int screen)
    {
        if (token == "all") { screen = AllScreens; return true; }
        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out screen)
            && screen is >= 1 and <= HighestScreen;
    }
}

internal sealed class CommandLineException(string message) : Exception(message);

internal sealed record ShadeInvocation(bool Help, bool Version, bool Diagnostics, bool Ephemeral, bool NoTray,
    bool NoActivate, string? SettingsDirectory, IReadOnlyList<ShadeOperation> Operations)
{
    private static bool NeedsInstance(ShadeVerb verb) => verb is ShadeVerb.Status or ShadeVerb.Exit;
    private static bool IsWindowState(ShadeVerb verb) => verb is ShadeVerb.Activate or ShadeVerb.Hide;

    // Reporting state or quitting is meaningless without an existing instance, so these never
    // start Shade.
    internal bool RequiresRunningInstance => Operations.Any(o => NeedsInstance(o.Verb));
    internal bool ChangesStartup => Ephemeral || NoTray || SettingsDirectory is not null;

    // A launch that finds an existing instance raises that window, which is what a user
    // double-clicking Shade expects. Reading state, hiding and quitting must not steal focus.
    internal IReadOnlyList<ShadeOperation> Forwarded()
    {
        var quiet = NoActivate || Operations.Any(o => NeedsInstance(o.Verb) || o.Verb == ShadeVerb.Hide);
        if (quiet || Operations.Any(o => o.Verb == ShadeVerb.Activate)) return Operations;
        return [.. Operations, new ShadeOperation(ShadeVerb.Activate)];
    }

    // A cold start opens and shows its own window, and has no earlier state to report or quit.
    internal IReadOnlyList<ShadeOperation> AtStartup() =>
        Operations.Where(o => !NeedsInstance(o.Verb) && !IsWindowState(o.Verb)).ToArray();
}

internal static class ShadeCommandLine
{
    internal static string Version => System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ShadeCommandLine).Assembly)
        ?.InformationalVersion.Split('+')[0] ?? "Unknown";

    internal static string Help => """
        Shade dims individual monitors. Run Shade with no options to open its window.

        Control options change the running instance immediately, or this one at startup:
          --global <0-100>            Global dimming percentage.
          --screen <number|all>       Select the screen for the options that follow.
          --level <0-100>             Set the selected screen's own level.
          --on | --off                Enable or disable dimming on the selected screen.
          --link | --unlink           Follow or stop following the global level.
          --allow-full-shade <on|off> Permit levels above 80%.
          --restore                   Remove dimming from every screen.
          --home-assistant <on|off>   Enable or disable the saved MQTT integration.
          --advanced <on|off>         Show or hide the Advanced section.
          --show | --hide             Show or hide the control window.
          --status                    Print the running instance's state.
          --exit                      Quit the running instance and remove overlays.

        Startup options apply only when Shade is not already running:
          --settings-directory <path> Use this settings directory.
          --ephemeral                 Do not load or save settings.
          --no-tray                   Closing the window exits instead of staying in the tray.

        Other options:
          --no-activate               Do not raise the running window.
          --diagnostics               Print detailed local failure information.
          --help, --version

        Screen numbers are the ones shown beside each monitor in Shade. Broker credentials are
        never accepted on a command line, because other programs on this computer can read it;
        set them in the Home Assistant section instead.

        Exit codes: 0 success, 1 failure, 2 unsupported platform, 3 running instance unreachable,
        4 invalid command line, 5 request rejected.

        Examples:
          Shade --global 40
          Shade --screen 2 --level 60 --screen 3 --off
          Shade --screen all --on --allow-full-shade on
          Shade --status

        """;

    internal static ShadeInvocation Parse(string[] args)
    {
        bool help = false, version = false, diagnostics = false, ephemeral = false, noTray = false, noActivate = false;
        string? directory = null;
        int? selected = null;
        var operations = new List<ShadeOperation>();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--help" or "-h" or "-?" or "/?": help = true; break;
                case "--version": version = true; break;
                case "--diagnostics": diagnostics = true; break;
                case "--no-activate": noActivate = true; break;
                case "--ephemeral": ephemeral = true; break;
                case "--no-tray": noTray = true; break;
                case "--settings-directory":
                    if (directory is not null) throw new CommandLineException("Specify one settings directory.");
                    directory = Path.GetFullPath(Value(args, ref index, argument));
                    break;
                case "--screen": selected = Screen(Value(args, ref index, argument)); break;
                case "--global": operations.Add(new(ShadeVerb.Global, ShadeOperation.AllScreens, Level(Value(args, ref index, argument)))); break;
                case "--level": operations.Add(new(ShadeVerb.Level, Selected(selected, argument), Level(Value(args, ref index, argument)))); break;
                case "--on": operations.Add(new(ShadeVerb.Enabled, Selected(selected, argument), 1)); break;
                case "--off": operations.Add(new(ShadeVerb.Enabled, Selected(selected, argument), 0)); break;
                case "--link": operations.Add(new(ShadeVerb.Link, Selected(selected, argument), 1)); break;
                case "--unlink": operations.Add(new(ShadeVerb.Link, Selected(selected, argument), 0)); break;
                case "--allow-full-shade": operations.Add(new(ShadeVerb.FullShade, ShadeOperation.AllScreens, Switch(Value(args, ref index, argument)))); break;
                case "--home-assistant": operations.Add(new(ShadeVerb.HomeAssistant, ShadeOperation.AllScreens, Switch(Value(args, ref index, argument)))); break;
                case "--advanced": operations.Add(new(ShadeVerb.Advanced, ShadeOperation.AllScreens, Switch(Value(args, ref index, argument)))); break;
                case "--restore": operations.Add(new(ShadeVerb.Restore)); break;
                case "--show": operations.Add(new(ShadeVerb.Activate)); break;
                case "--hide": operations.Add(new(ShadeVerb.Hide)); break;
                case "--status": operations.Add(new(ShadeVerb.Status)); break;
                case "--exit": operations.Add(new(ShadeVerb.Exit)); break;
                default:
                    throw new CommandLineException($"Unknown option '{Describe(argument)}'. Run Shade --help.");
            }
        }
        if (ephemeral && directory is not null)
            throw new CommandLineException("Specify a settings directory or --ephemeral, not both.");
        return new(help, version, diagnostics, ephemeral, noTray, noActivate, directory, operations);
    }

    private static string Value(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length) throw new CommandLineException($"Option '{option}' needs a value.");
        var value = args[++index];
        if (value.StartsWith("--", StringComparison.Ordinal))
            throw new CommandLineException($"Option '{option}' needs a value.");
        return value;
    }

    private static int Selected(int? screen, string option) => screen
        ?? throw new CommandLineException($"Use --screen before '{option}'.");

    private static int Level(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level is >= 0 and <= DimLevel.Maximum
            ? level : throw new CommandLineException($"'{Describe(value)}' is not a percentage between 0 and {DimLevel.Maximum}.");

    private static int Switch(string value) => value switch
    {
        "on" => 1,
        "off" => 0,
        _ => throw new CommandLineException($"'{Describe(value)}' is not 'on' or 'off'.")
    };

    private static int Screen(string value)
    {
        if (value == "all") return ShadeOperation.AllScreens;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var screen)
            && screen is >= 1 and <= ShadeOperation.HighestScreen
            ? screen : throw new CommandLineException($"'{Describe(value)}' is not a screen number or 'all'.");
    }

    // Arguments are echoed back in errors, so they must not be able to inject control characters
    // or unbounded text into a console line or a forwarded protocol line.
    private static string Describe(string value)
    {
        var text = new StringBuilder();
        foreach (var character in value.Length > 40 ? value[..40] : value)
            text.Append(character is >= ' ' and <= '~' ? character : '?');
        return text.Append(value.Length > 40 ? "..." : "").ToString();
    }
}
