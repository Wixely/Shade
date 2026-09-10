using CupriFace;
using SkiaSharp;

namespace Shade;

public sealed class ShadeApp : CupriApp
{
    private readonly ShadeModel model;
    public ShadeApp(IDimmingBackend backend) => model = new(backend);
    public override string Title => "Shade - feasibility prototype";
    public override int Width => 680;
    public override int Height => 650;
    public override SKColor Background => new(0x14, 0x1c, 0x24);
    public override bool DarkWindowChrome => true;
    public override object Model => model;
    public override double RefreshIntervalSeconds => 0.2;
    public override string Html => """
        <body><main>
          <h1>Shade</h1>
          <p class="intro">Independent dimming for your displays</p>
          <div class="actions"><cupri-button class="restore">Restore all displays</cupri-button></div>
          <p class="status" role="status">{{Status}}</p>
          <p class="hint">0% leaves a display unchanged. Prototype limit: 80% dimming.</p>
          <section class="display" data-repeat="Displays">
            <h2>{{Label}}</h2>
            <p class="geometry">{{Geometry}}</p>
            <div class="level">
              <cupri-slider min="0" max="80" step="1" value="{{Level}}" aria-label="{{SliderLabel}}"></cupri-slider>
              <span class="value">{{Level}}%</span>
            </div>
            <cupri-button class="reset" data-display="{{Id}}">Restore this display</cupri-button>
          </section>
          <p class="hint">Closing Shade restores every display. Display changes restore all shading and require a restart.</p>
          <p class="hint">Home Assistant and Linux shading are not implemented yet.</p>
        </main></body>
        """;
    public override string Css => """
        body { background:#141c24; color:#eaf0f6; font-family:sans-serif; font-size:16px; }
        main { padding:28px; }
        h1 { font-size:32px; margin:0 0 6px 0; }
        h2 { font-size:19px; margin:0 0 6px 0; }
        p { margin:0 0 14px 0; }
        .intro { color:#b8c9d8; margin-bottom:22px; }
        .actions { margin-bottom:14px; }
        .status { color:#8ce0cb; }
        .hint, .geometry { color:#b8c9d8; font-size:14px; }
        .display { background:#202e3b; padding:18px; border-radius:10px; margin-bottom:16px; }
        .level { display:flex; align-items:center; gap:16px; margin-bottom:16px; }
        cupri-slider { flex:1; height:32px; }
        .value { width:52px; text-align:right; font-weight:bold; }
        cupri-button { padding:10px 16px; }
        """;
    public override void Configure(CupriDocument document)
    {
        document.OnClick(".restore", _ => model.RestoreAll());
        document.OnClick(".reset", e => model.Restore(e.Element.GetAttribute("data-display")));
    }
}

public sealed class ShadeModel
{
    private readonly IDimmingBackend backend;
    private string? error;
    public ShadeModel(IDimmingBackend backend)
    {
        this.backend = backend;
        Displays = backend.Displays.Select(d => new DisplayModel(d, backend, ReportError)).ToArray();
    }
    public DisplayModel[] Displays { get; }
    public string Status => error ?? backend.Status;
    private void ReportError(Exception ex) => error = ex is ArgumentException
        ? "Invalid display or dimming level. Restore all and restart Shade."
        : "Dimming is unavailable. Close Shade to restore displays, then restart.";
    public void RestoreAll()
    {
        try { backend.RestoreAll(); error = null; }
        catch (Exception ex) { ReportError(ex); }
    }
    public void Restore(string? id)
    {
        if (id is null) return;
        try { backend.SetLevel(id, 0); error = null; }
        catch (Exception ex) { ReportError(ex); }
    }
}

public sealed class DisplayModel(Display display, IDimmingBackend backend, Action<Exception> reportError)
{
    public string Id => display.Id;
    public string Label => display.Label;
    public string SliderLabel => $"Dimming for {Label}";
    public string Geometry => $"{display.Width} x {display.Height} at ({display.X}, {display.Y})";
    public int Level
    {
        get => backend.GetLevel(Id);
        set
        {
            try { backend.SetLevel(Id, DimLevel.Validate(value)); }
            catch (Exception ex) { reportError(ex); }
        }
    }
}
