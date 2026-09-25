using Shade;

internal static class CommandLineTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void Rejects(string message, params string[] args)
    {
        try { ShadeCommandLine.Parse(args); }
        catch (CommandLineException exception)
        {
            Check(!exception.Message.Contains('\n') && !exception.Message.Contains('\r'),
                "Usage errors must stay on one line: " + message);
            return;
        }
        throw new Exception("Accepted an invalid command line: " + message);
    }

    internal static void Run()
    {
        var idle = ShadeCommandLine.Parse([]);
        Check(idle.Operations.Count == 0 && !idle.ChangesStartup && !idle.RequiresRunningInstance, "A bare launch requested work");

        var ordered = ShadeCommandLine.Parse(["--screen", "2", "--level", "60", "--screen", "3", "--off"]);
        Check(ordered.Operations.SequenceEqual([
            new ShadeOperation(ShadeVerb.Level, 2, 60), new ShadeOperation(ShadeVerb.Enabled, 3, 0)]),
            "Screen selection did not follow the order given");

        Check(ShadeCommandLine.Parse(["--screen", "all", "--on", "--link"]).Operations.SequenceEqual([
            new ShadeOperation(ShadeVerb.Enabled, 0, 1), new ShadeOperation(ShadeVerb.Link, 0, 1)]),
            "'all' did not address every screen");

        Check(ShadeCommandLine.Parse(["--global", "40", "--allow-full-shade", "on", "--restore", "--advanced", "off",
            "--home-assistant", "on"]).Operations.SequenceEqual([
                new ShadeOperation(ShadeVerb.Global, 0, 40), new ShadeOperation(ShadeVerb.FullShade, 0, 1),
                new ShadeOperation(ShadeVerb.Restore), new ShadeOperation(ShadeVerb.Advanced, 0, 0),
                new ShadeOperation(ShadeVerb.HomeAssistant, 0, 1)]),
            "Global and switch options did not parse");

        var startup = ShadeCommandLine.Parse(["--ephemeral", "--no-tray", "--diagnostics"]);
        Check(startup is { Ephemeral: true, NoTray: true, Diagnostics: true } && startup.ChangesStartup, "Startup options did not parse");
        Check(ShadeCommandLine.Parse(["--diagnostics"]).ChangesStartup == false, "Diagnostics is not a startup change");
        Check(ShadeCommandLine.Parse(["--settings-directory", "data"]).SettingsDirectory is { Length: > 0 }, "Settings directory did not parse");

        Rejects("unknown option", "--frobnicate");
        Rejects("control characters in an unknown option", "--oh\nno");
        Rejects("screen-scoped option without a screen", "--level", "50");
        Rejects("enable without a screen", "--on");
        Rejects("percentage above the maximum", "--global", "101");
        Rejects("negative percentage", "--global", "-1");
        Rejects("non-numeric percentage", "--global", "half");
        Rejects("missing value", "--global");
        Rejects("an option used as a value", "--global", "--status");
        Rejects("screen zero", "--screen", "0");
        Rejects("screen above the supported count", "--screen", "65");
        Rejects("a switch that is not on or off", "--allow-full-shade", "yes");
        Rejects("a settings directory with ephemeral mode", "--settings-directory", "data", "--ephemeral");
        Rejects("two settings directories", "--settings-directory", "one", "--settings-directory", "two");

        // A duplicate launch raises the existing window unless the request is meant to be quiet.
        Check(ShadeCommandLine.Parse(["--global", "40"]).Forwarded().Any(o => o.Verb == ShadeVerb.Activate), "A forwarded change did not raise the window");
        Check(ShadeCommandLine.Parse([]).Forwarded().SequenceEqual([new ShadeOperation(ShadeVerb.Activate)]), "A bare duplicate launch did not raise the window");
        Check(!ShadeCommandLine.Parse(["--status"]).Forwarded().Any(o => o.Verb == ShadeVerb.Activate), "Reading state stole focus");
        Check(!ShadeCommandLine.Parse(["--exit"]).Forwarded().Any(o => o.Verb == ShadeVerb.Activate), "Quitting stole focus");
        Check(!ShadeCommandLine.Parse(["--hide"]).Forwarded().Any(o => o.Verb == ShadeVerb.Activate), "Hiding raised the window");
        Check(!ShadeCommandLine.Parse(["--no-activate", "--global", "40"]).Forwarded().Any(o => o.Verb == ShadeVerb.Activate), "--no-activate raised the window");
        Check(ShadeCommandLine.Parse(["--show", "--global", "40"]).Forwarded().Count(o => o.Verb == ShadeVerb.Activate) == 1, "Explicit --show was duplicated");

        // A cold start opens its own window and has no earlier state to report or quit.
        Check(ShadeCommandLine.Parse(["--global", "40", "--status", "--hide", "--exit", "--show"]).AtStartup()
            .SequenceEqual([new ShadeOperation(ShadeVerb.Global, 0, 40)]), "Startup kept requests that need a running instance");
        Check(ShadeCommandLine.Parse(["--status"]).RequiresRunningInstance && ShadeCommandLine.Parse(["--exit"]).RequiresRunningInstance,
            "Status and exit must not start Shade");
        Check(!ShadeCommandLine.Parse(["--global", "40"]).RequiresRunningInstance, "A control change refused to start Shade");

        Check(ShadeCommandLine.Help.Contains("--global") && ShadeCommandLine.Help.Contains("credentials"),
            "Help must list the controls and the credential exclusion");

        Wire();
    }

    private static void Wire()
    {
        // Every operation must survive the forwarded form exactly: it is what the running instance acts on.
        ShadeOperation[] operations = [
            new(ShadeVerb.Global, 0, 40), new(ShadeVerb.Level, 3, 100), new(ShadeVerb.Level, 0, 0),
            new(ShadeVerb.Enabled, 1, 1), new(ShadeVerb.Enabled, 0, 0), new(ShadeVerb.Link, 64, 1),
            new(ShadeVerb.FullShade, 0, 1), new(ShadeVerb.HomeAssistant, 0, 0), new(ShadeVerb.Advanced, 0, 1),
            new(ShadeVerb.Restore), new(ShadeVerb.Activate), new(ShadeVerb.Hide), new(ShadeVerb.Status), new(ShadeVerb.Exit)];
        foreach (var operation in operations)
        {
            var line = operation.Write();
            Check(line.All(character => character is >= ' ' and <= '~'), "A forwarded line left printable ASCII: " + line);
            Check(ShadeOperation.TryRead(line, out var read) && read == operation, "Operation did not survive the wire: " + line);
        }

        // The running instance accepts only this vocabulary, in exactly this shape.
        string[] malformed = [
            "", " ", "global", "global 40 extra", "global  40", "global -1", "global 101", "global x", "GLOBAL 40",
            "level 40", "level 0 40", "level 65 40", "level all", "level all 101", "enabled 1 yes", "enabled 1",
            "link 1 ON", "full-shade", "full-shade on off", "home-assistant maybe", "advanced 1",
            "restore now", "activate please", "status all", "exit 0", "nonsense", "end", "shade 1 100"];
        foreach (var line in malformed)
            Check(!ShadeOperation.TryRead(line, out _), "Accepted a malformed forwarded line: '" + line + "'");

        // Screen labels come from display hardware and failures from exceptions; neither may break framing.
        Check(!InstanceChannel.Single("one\ntwo\r\nthree").Contains('\n'), "A reply line kept a newline");
        Check(!InstanceChannel.Single("tab\there").Contains('\t'), "A reply line kept a control character");
        Check(InstanceChannel.Single(new string('x', 500)).Length <= 200, "A reply line was not length-capped");
        Check(InstanceChannel.Single("\n\n") == "unavailable", "An empty label produced an empty token");
        Check(InstanceChannel.NameFor("a") != InstanceChannel.NameFor("b"), "Channel names collide across sessions");
        Check(InstanceChannel.NameFor("a") == InstanceChannel.NameFor("a"), "Channel names are not stable");
        Check(!InstanceChannel.NameFor("domain\\user:1").Contains('\\'), "Channel names must not carry account text");
    }
}
