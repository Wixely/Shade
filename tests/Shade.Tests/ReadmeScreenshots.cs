using Shade;
using SkiaSharp;

internal static class ReadmeScreenshots
{
    public static void Capture()
    {
        // Render the real Cupri UI with synthetic data; never read desktop or saved settings.
        using var backend = new FakeBackend();
        backend.ReplaceDisplays([
            new("left", "Portrait display", -1080, 0, 1080, 1920),
            new("main", "Main display", 0, 280, 2560, 1440),
            new("right", "Side display", 2560, 280, 1920, 1080)]);
        using var integration = new HomeAssistantIntegration();
        var app = new ShadeApp(backend, integration);
        var model = (ShadeModel)app.Model;
        model.SetGlobal(35);
        model.Toggle("left");
        model.Toggle("main");
        using var document = app.CreateDocument();
        var output = Path.GetFullPath("artifacts/readme");
        Directory.CreateDirectory(output);
        void Save(string name, int height)
        {
            document.Refresh();
            using var frame = document.RenderToImage(800, height, app.Background);
            using var data = frame.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            data.SaveTo(stream);
        }
        Save("screens", 800);
        model.ToggleAdvanced();
        model.SetBindable(nameof(ShadeModel.AllowFullShade), true);
        model.SetBindable("UseGlobal_left", false);
        model.SetBindable("Level_left", 20);
        Save("advanced", 1050);
        model.ToggleAdvanced();
        model.ToggleAutomation();
        model.SetBindable(nameof(ShadeModel.BrokerHost), "mqtt.example.com");
        Save("home-assistant", 1150);
        Console.WriteLine("Captured README screenshots with synthetic displays and no credentials: " + output);
    }
}
