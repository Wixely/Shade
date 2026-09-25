using System.IO.Pipes;
using System.Text;
using Shade;

// Exercises a real control channel against a live model: the request crosses a named pipe, is
// applied on a separate interface thread, and the reply comes back from that thread.
internal static class InstanceChannelTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    internal static async Task Run()
    {
        var name = InstanceChannel.NameFor("Shade.Tests." + Guid.NewGuid().ToString("N"));
        using var backend = new FakeBackend();
        using var integration = new HomeAssistantIntegration();
        var requests = new InstanceRequests();
        var shutdowns = 0;
        var model = new ShadeModel(backend, integration, requests, () => shutdowns++, "Shade.Tests.Window");
        using var server = InstanceServer.TryStart(name, requests) ?? throw new Exception("The control channel did not start");

        // Only this pump touches the model, exactly as the interface thread does in the application.
        using var pumping = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!pumping.IsCancellationRequested)
            {
                _ = model.Revision;
                try { await Task.Delay(2, pumping.Token); } catch (OperationCanceledException) { return; }
            }
        });
        try
        {
            Check(InstanceServer.TryStart(name, requests) is null, "A second instance took an owned channel");

            var accepted = await Send(name, [new(ShadeVerb.Global, 0, 40), new(ShadeVerb.Enabled, 0, 1)]);
            Check(accepted.Ok, "Global dimming was rejected: " + accepted.Message);
            var state = await Status(name);
            Check(state.Contains("global 40"), "The global level did not change");
            Check(state.Any(line => line.StartsWith("screen 1 on level 40 global ", StringComparison.Ordinal)), "Screen 1 did not follow the global level");
            Check(state.Any(line => line.StartsWith("screen 2 on level 40 global ", StringComparison.Ordinal)), "Screen 2 did not follow the global level");
            Check(state.Contains("full-shade off") && state.Contains("home-assistant off"), "Reported state omitted switches");

            Check((await Send(name, [new(ShadeVerb.Level, 1, 55)])).Ok, "An individual level was rejected");
            state = await Status(name);
            Check(state.Any(line => line.StartsWith("screen 1 on level 55 independent ", StringComparison.Ordinal)), "Screen 1 kept the global level");
            Check(state.Any(line => line.StartsWith("screen 2 on level 40 global ", StringComparison.Ordinal)), "One screen's change moved another");

            // Levels above the default maximum need the same opt-in the interface requires.
            var refused = await Send(name, [new(ShadeVerb.Global, 0, 90)]);
            Check(!refused.Ok && refused.Message.Contains("100%"), "A level above the maximum was accepted: " + refused.Message);
            Check((await Status(name)).Contains("global 40"), "A refused level still changed the global level");
            Check((await Send(name, [new(ShadeVerb.FullShade, 0, 1), new(ShadeVerb.Global, 0, 90)])).Ok, "Full shading did not permit a high level");
            Check((await Status(name)).Contains("global 90"), "The high level was not applied");
            Check((await Send(name, [new(ShadeVerb.FullShade, 0, 0)])).Ok, "Full shading could not be switched off");
            Check((await Status(name)).Contains("global 80"), "Switching full shading off did not clamp the global level");

            // An unknown screen rejects the whole request rather than applying part of it.
            var unknown = await Send(name, [new(ShadeVerb.Global, 0, 30), new(ShadeVerb.Level, 9, 10)]);
            Check(!unknown.Ok && unknown.Message.Contains("Screen 9"), "An unknown screen was accepted: " + unknown.Message);
            Check((await Status(name)).Contains("global 80"), "A rejected request still applied its earlier operations");

            Check((await Send(name, [new(ShadeVerb.Restore)])).Ok, "Restore was rejected");
            state = await Status(name);
            Check(state.Any(line => line.StartsWith("screen 1 off level 0 ", StringComparison.Ordinal))
                && state.Any(line => line.StartsWith("screen 2 off level 0 ", StringComparison.Ordinal)), "Restore left dimming in place");

            Check((await Send(name, [new(ShadeVerb.Advanced, 0, 1)])).Ok, "Advanced could not be opened");
            Check((await Status(name)).Contains("advanced on"), "Advanced did not open");

            // Enabling the integration without a saved broker must fail loudly, not silently.
            var automation = await Send(name, [new(ShadeVerb.HomeAssistant, 0, 1)]);
            Check(!automation.Ok && automation.Message.Contains("broker"), "Home Assistant was enabled without a broker: " + automation.Message);

            await Malformed(name);

            // A reply must reach the caller before the instance acts on a quit request.
            var quit = await Send(name, [new(ShadeVerb.Exit)]);
            Check(quit.Ok, "A quit request was rejected: " + quit.Message);
            var waited = 0;
            while (shutdowns == 0 && waited < 500) { await Task.Delay(10); waited++; }
            Check(shutdowns == 1, "The instance did not shut down after replying");
        }
        finally
        {
            pumping.Cancel();
            try { await pump; } catch (OperationCanceledException) { }
        }
    }

    private static Task<InstanceReply> Send(string name, ShadeOperation[] operations) =>
        InstanceChannel.SendAsync(name, operations);

    private static async Task<IReadOnlyList<string>> Status(string name)
    {
        var reply = await InstanceChannel.SendAsync(name, [new ShadeOperation(ShadeVerb.Status)]);
        Check(reply.Ok, "State could not be read: " + reply.Message);
        return reply.Lines;
    }

    // Input arriving on the channel is untrusted: it must be refused without changing state, and the
    // channel must keep serving afterwards.
    private static async Task Malformed(string name)
    {
        Check((await Raw(name, "global 40 extra\nend\n")).StartsWith("error ", StringComparison.Ordinal), "An overlong line was accepted");
        Check((await Raw(name, "wipe-disk\nend\n")).StartsWith("error ", StringComparison.Ordinal), "An unknown verb was accepted");
        Check((await Raw(name, "global 30\nnonsense\nend\n")).StartsWith("error ", StringComparison.Ordinal), "A request with one bad line was accepted");
        Check((await Raw(name, new string('x', InstanceChannel.MaximumRequestBytes + 64) + "\nend\n")).StartsWith("error ", StringComparison.Ordinal),
            "An oversized request was accepted");
        Check((await Raw(name, string.Concat(Enumerable.Repeat("restore\n", InstanceChannel.MaximumOperations + 4)) + "end\n"))
            .StartsWith("error ", StringComparison.Ordinal), "A request with too many operations was accepted");
        Check((await Raw(name, "global 40\nend\n", [0x80])).StartsWith("error ", StringComparison.Ordinal), "A non-ASCII request was accepted");
        // Nothing above changed state, and the channel still answers.
        var state = await Status(name);
        Check(state.Contains("global 80"), "Refused input changed the global level");
    }

    private static async Task<string> Raw(string name, string request, byte[]? prefix = null)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(10_000);
        await ReadAsync(pipe); // greeting
        if (prefix is not null) await pipe.WriteAsync(prefix);
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(request));
        await pipe.FlushAsync();
        return await ReadAsync(pipe);
    }

    private static async Task<string> ReadAsync(PipeStream pipe)
    {
        var text = new StringBuilder();
        var buffer = new byte[256];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!text.ToString().Split('\n').Contains("end"))
        {
            var read = await pipe.ReadAsync(buffer, deadline.Token);
            if (read == 0) break;
            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        return text.ToString();
    }
}
