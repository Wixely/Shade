using CupriFace;
using CupriFace.Binding;
using SkiaSharp;

namespace Shade;

public sealed class ShadeApp : CupriApp
{
    private readonly ShadeModel model;
    private long renderedRevision = -1;
    private CupriDocument? document;
    public ShadeApp(IDimmingBackend backend, HomeAssistantIntegration? integration = null, bool closeToTray = true,
        InstanceRequests? requests = null, Action? shutdown = null)
    {
        CloseToTray = closeToTray && OperatingSystem.IsWindows();
        model = new(backend, integration, requests, shutdown, Title)
        {
            LifecycleHint = CloseToTray ? "Closing this window keeps Shade running. Open it from the tray, or choose Exit Shade to quit. Ctrl+Alt+Shift+R restores every screen."
                : OperatingSystem.IsWindows() ? "Closing this window exits Shade and removes its overlays. Ctrl+Alt+Shift+R restores every screen."
                    : "Closing this window exits Shade and removes its overlays. The recovery shortcut is shown in the status above."
        };
    }
    public override bool CloseToTray { get; }
    public override string TrayCloseLabel => "Exit Shade";
    public override string Title => "Shade";
    private byte[]? icon;
    public override byte[] Icon => icon ??= EmbeddedAsset("Shade.Assets.shade.png").ReadBytes();
    public override int Width => 720;
    public override int Height => 740;
    public override SKColor Background => new(0x14, 0x1c, 0x24);
    public override bool DarkWindowChrome => true;
    public override object Model => model;
    public override CupriFace.Components.ComponentRegistry Components => base.Components.Register(new AccessiblePasswordComponent());
    // CupriFace checks this on its UI thread. Stable state requests no refresh or GPU swap.
    public override double RefreshIntervalSeconds => model.Revision == renderedRevision ? 0 : 0.001;
    public override PresentInfo Present(float windowWidth, float windowHeight)
    {
        // CupriFace 0.20.0's software host publishes UIA only when pixels are damaged.
        // Offscreen state changes must still publish fresh accessibility values. Force a frame
        // only for a new backend revision; idle remains clean. Remove once the host decouples UIA.
        if (renderedRevision != model.ObservedRevision) document?.InvalidateRetainedFrame();
        renderedRevision = model.ObservedRevision;
        return base.Present(windowWidth, windowHeight);
    }
    public override string Html => """
        <body><main>
          <header><div class="header-title"><h1><cupri-image class="brand-icon" src="Shade.Assets.shade.png" alt=""></cupri-image>Shade</h1><div class="header-actions"><cupri-button class="about-toggle" variant="ghost" data-set-path="AboutOpen" data-set-value="true" aria-haspopup="dialog">About</cupri-button><cupri-button class="automation-toggle" variant="ghost" aria-expanded="{{AutomationOpen}}" aria-controls="automation-panel">{{AutomationLabel}}</cupri-button></div></div><p class="intro">A little less light. Just where you want it.</p></header>
          <section class="global">
            <div class="section-title"><h2>Global dimming</h2><span class="global-value">{{GlobalSummary}}</span></div>
            <cupri-slider class="global-slider" min="0" max="{{Maximum}}" step="1" value="{{GlobalLevel}}" aria-label="Global dimming"></cupri-slider>
            <div class="scale"><span>No dimming</span><span>{{Maximum}}% dimming</span></div>
            <div class="global-presets" role="group" aria-label="Quick global dimming">
              <cupri-button class="global-preset" variant="ghost" data-set-path="GlobalLevel" data-set-value="0">0%</cupri-button>
              <cupri-button class="global-preset" variant="ghost" data-set-path="GlobalLevel" data-set-value="{{GlobalPresetOne}}">{{GlobalPresetOne}}%</cupri-button>
              <cupri-button class="global-preset" variant="ghost" data-set-path="GlobalLevel" data-set-value="{{GlobalPresetTwo}}">{{GlobalPresetTwo}}%</cupri-button>
              <cupri-button class="global-preset" variant="ghost" data-set-path="GlobalLevel" data-set-value="{{GlobalPresetThree}}">{{GlobalPresetThree}}%</cupri-button>
              <cupri-button class="global-preset" variant="ghost" data-set-path="GlobalLevel" data-set-value="{{Maximum}}">{{Maximum}}%</cupri-button>
            </div>
            <p class="hint">Affects enabled screens using global. Disabled and independent screens stay unchanged.</p>
          </section>
          <div class="content-scroll">
          <div class="screens-panel" style="{{ScreensStyle}}">
          <div class="section-title"><h2>Your screens</h2><cupri-button class="restore" variant="ghost">Restore all displays</cupri-button></div>
          <p class="hint">Click a screen to enable or disable dimming. Choose Use global in Advanced.</p>
          <div class="map-scroll" aria-label="Screen arrangement">
            <div class="map-canvas" style="{{CanvasStyle}}">
              <cupri-button class="monitor {{TileState}}" data-repeat="Displays" data-display="{{Id}}" style="{{TileStyle}}" aria-label="{{ToggleLabel}}" aria-pressed="{{Enabled}}">
                <span class="screen-number">{{Number}}</span><span class="screen-state">{{StateLabel}}</span>
              </cupri-button>
            </div>
          </div>
          <cupri-button class="advanced" variant="ghost" aria-expanded="{{AdvancedOpen}}" aria-controls="advanced-panel">{{AdvancedLabel}}</cupri-button>
          <section id="advanced-panel" style="{{AdvancedStyle}}">
            <div class="global-option"><cupri-switch class="allow-full-shade" checked="{{AllowFullShade}}" aria-label="Allow 100% shading"></cupri-switch><span>Allow 100% shading (fully black)</span></div>
            <p class="hint">Fine-tune a screen without changing the others.</p>
            <section class="display" data-repeat="Displays">
              <h2>{{Number}} · {{Label}}</h2>
              <p class="geometry">{{Geometry}}</p>
              <div class="global-option">
                <cupri-switch class="use-global" checked="{{UseGlobal}}" data-bind-checked="{{GlobalBindingPath}}" aria-label="{{GlobalLabel}}"></cupri-switch>
                <span>Use global</span><span class="hint">{{ModeLabel}}</span>
              </div>
              <div class="level">
                <cupri-slider class="individual-slider" min="0" max="{{Maximum}}" step="1" value="{{Level}}" data-bind-value="{{BindingPath}}" aria-label="{{SliderLabel}}"></cupri-slider>
                <span class="value">{{Level}}%</span>
              </div>
              <p class="hint">{{IdentityNote}}</p>
              <cupri-button class="assign-screen" data-display="{{Id}}" style="{{AssignStyle}}" variant="ghost">Assign a persistent identity</cupri-button>
              <cupri-button class="detach-screen" data-display="{{Id}}" style="{{DetachStyle}}" variant="ghost">Detach this identity</cupri-button>
              <cupri-button class="reset" data-display="{{Id}}" variant="ghost">Restore this display</cupri-button>
            </section>
          </section>
          </div>
          <section id="assignment-panel" class="display" style="{{AssignmentStyle}}">
            <h2>Assign screen identity</h2>
            <p class="geometry">{{AssignmentTarget}}</p>
            <p class="hint">This screen has no reliable unique hardware serial. Assignment remembers this connection, not physical identity. If identical monitors move between connections, reuse the correct assignment here. Assignment leaves the screen undimmed.</p>
            <cupri-textfield class="assignment-name" value="{{AssignmentName}}" aria-label="Screen identity name" placeholder="For example, Left portrait"></cupri-textfield>
            <cupri-button class="assignment-create">Create identity</cupri-button>
            <p class="hint">Or reuse a disconnected identity to retain its settings and Home Assistant entity IDs:</p>
            <cupri-button class="assignment-reuse" variant="ghost" data-repeat="AvailableAssignments" data-assignment="{{Id}}">Reuse {{Name}}</cupri-button>
            <p class="status" role="status">{{AssignmentStatus}}</p>
          </section>
          <section id="automation-panel" class="display" style="{{AutomationStyle}}">
            <h2>Home Assistant</h2>
            <p class="hint">Connect to the MQTT 5 broker used by Home Assistant. Dimming uses the same 0–{{Maximum}}% scale. Only identified screens are published.</p>
            <label for="broker-host">Broker hostname</label>
            <cupri-textfield id="broker-host" value="{{BrokerHost}}" aria-label="Broker hostname"></cupri-textfield>
            <label for="broker-port">Port</label>
            <cupri-textfield id="broker-port" inputmode="numeric" value="{{BrokerPort}}" aria-label="Broker port"></cupri-textfield>
            <div class="global-option"><cupri-switch checked="{{BrokerTls}}" aria-label="Use TLS"></cupri-switch><span>Use TLS (system certificate trust)</span></div>
            <label for="broker-user">Username</label>
            <cupri-textfield id="broker-user" value="{{BrokerUsername}}" aria-label="Broker username"></cupri-textfield>
            <label for="broker-password">Password</label>
            <cupri-password id="broker-password" value="{{BrokerPassword}}" aria-label="Broker password" autocomplete="off"></cupri-password>
            <p class="hint">{{PasswordHint}}</p>
            <cupri-button class="broker-forget" variant="ghost">Forget saved password</cupri-button>
            <div class="actions"><cupri-button class="broker-test">Test connection</cupri-button><cupri-button class="broker-enable">Save and enable</cupri-button></div>
            <div class="actions"><cupri-button class="broker-disable" variant="ghost">Disable integration</cupri-button><cupri-button class="broker-remove" variant="ghost">Remove discovery</cupri-button></div>
            <p class="status" role="status">{{AutomationStatus}}</p>
            <cupri-button class="broker-recover" variant="ghost" style="{{AutomationRecoveryStyle}}">{{AutomationRecoveryLabel}}</cupri-button>
          </section>
          <p class="status" role="status">{{Status}}</p>
          <cupri-button class="recovery-setup" style="{{RecoverySetupStyle}}">Set up recovery shortcut</cupri-button>
          <cupri-button class="settings-recover" variant="ghost" style="{{SettingsRecoveryStyle}}">{{SettingsRecoveryLabel}}</cupri-button>
          <p class="footnote">{{LifecycleHint}}</p>
          </div>
        </main>
        <cupri-dialog open="{{AboutOpen}}" aria-label="About Shade">
          <cupri-image class="about-icon" src="Shade.Assets.shade.png" alt=""></cupri-image>
          <h2>About Shade</h2>
          <p class="about-version">Version {{AppVersion}}</p>
          <p>A little less light. Just where you want it.</p>
          <a class="project-link" href="{{ProjectUrl}}" target="_blank">View Shade on GitHub</a>
          <cupri-button class="about-close" data-cupri-dismiss="true">Close</cupri-button>
        </cupri-dialog>
        </body>
        """;
    public override string Css => """
        body { background:#141c24; color:#eaf0f6; font-family:sans-serif; font-size:15px; --cupri-accent:#64cdb6; }
        main { padding:28px; height:100vh; box-sizing:border-box; display:flex; flex-direction:column; overflow:auto; }
        header { flex-shrink:0; }
        .content-scroll { flex:1; min-height:80px; overflow:auto; padding-right:12px; }
        h1 { display:flex; align-items:center; gap:10px; font-size:30px; margin:0 0 4px 0; }
        .brand-icon { width:36px; height:36px; }
        h2 { font-size:17px; margin:0; }
        p { margin:0 0 12px 0; }
        .intro { color:#a8bccb; margin-bottom:22px; }
        .global { background:#202e3b; padding:18px; border-radius:12px; margin-bottom:22px; flex-shrink:0; }
        .section-title { display:flex; align-items:center; justify-content:space-between; gap:12px; margin-bottom:10px; }
        .global-value { color:#8ce0cb; font-weight:bold; }
        .global-slider { display:block; height:30px; margin:6px 0; }
        .scale { display:flex; justify-content:space-between; color:#a8bccb; font-size:12px; margin-bottom:10px; }
        .global-presets { display:flex; gap:8px; margin-bottom:12px; }
        .global-preset { flex:1; padding:7px 4px; font-size:13px; text-align:center; }
        .hint, .geometry { color:#a8bccb; font-size:13px; }
        .global .hint { margin:0; }
        .map-scroll { overflow:auto; max-height:300px; padding:8px; background:#101820; border-radius:12px; margin-bottom:18px; }
        .map-canvas { position:relative; margin:0 auto; }
        .monitor { position:absolute; box-sizing:border-box; padding:6px; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:6px; border:2px solid #526474; background:#243340; color:#a8bccb; border-radius:7px; }
        .monitor.on { background:#234e49; border-color:#64cdb6; color:#e0fff7; }
        .monitor[data-hover] { border-color:#d2f4ed; }
        .screen-number { font-size:24px; font-weight:bold; }
        .screen-state { font-size:12px; }
        .restore, .reset { font-size:13px; padding:7px 10px; }
        .advanced { display:block; padding:12px 14px; margin-bottom:14px; }
        .display { background:#202e3b; padding:16px; border-radius:10px; margin-bottom:12px; }
        .geometry { margin-top:6px; }
        .global-option { display:flex; align-items:center; gap:12px; margin-bottom:14px; }
        .level { display:flex; align-items:center; gap:16px; margin-bottom:12px; }
        .individual-slider { flex:1; height:30px; }
        .value { width:48px; text-align:right; font-weight:bold; }
        .status { color:#8ce0cb; font-size:12px; margin-top:8px; }
        .footnote { color:#91a6b7; font-size:12px; }
        .header-title { display:flex; align-items:center; justify-content:space-between; gap:12px; }
        .header-actions { display:flex; align-items:center; gap:8px; }
        .about-toggle { padding:7px 10px; font-size:13px; }
        .cupri-dialog-panel { background:#202e3b; color:#eaf0f6; width:360px; max-width:90vw; box-sizing:border-box; }
        .about-icon { width:64px; height:64px; margin-bottom:12px; }
        .about-version { color:#a8bccb; margin-top:8px; }
        .project-link { display:block; color:#8ce0cb; text-decoration:underline; margin-bottom:22px; }
        .about-close { color:#102820; }
        .automation-toggle { padding:7px 10px; font-size:13px; }
        #automation-panel h2 { margin-bottom:12px; }
        #automation-panel label { display:block; margin-bottom:5px; }
        #automation-panel cupri-textfield, #automation-panel cupri-password { display:block; min-height:42px; width:100%; box-sizing:border-box; padding:10px; margin-bottom:14px; background:#101820; color:#eaf0f6; border:1px solid #526474; border-radius:5px; }
        .cupri-tf-text { color:#eaf0f6; }
        .broker-test, .broker-enable, .assignment-create { color:#102820; }
        .broker-forget { margin-bottom:14px; }
        .actions { display:flex; flex-wrap:wrap; gap:10px; margin-bottom:12px; }
        .assign-screen, .detach-screen, .assignment-create, .assignment-reuse { display:block; margin-bottom:12px; }
        #assignment-panel h2 { margin-bottom:12px; }
        .assignment-name { display:block; min-height:42px; padding:10px; margin-bottom:14px; background:#101820; color:#eaf0f6; }
        """;
    public override void Configure(CupriDocument document)
    {
        this.document = document;
        document.OnClick(".restore", _ => model.RestoreAll());
        document.OnClick(".reset", e => model.Restore(e.Element.GetAttribute("data-display")));
        document.OnClick(".monitor", e => model.Toggle(e.Element.GetAttribute("data-display")));
        document.OnClick(".advanced", _ => model.ToggleAdvanced());
        document.OnClick(".automation-toggle", _ =>
        {
            model.ToggleAutomation();
            document.Refresh();
            ResetScroll(document.Root);
        });
        document.OnClick(".broker-test", _ => model.ConfigureAutomation(false));
        document.OnClick(".broker-enable", _ => model.ConfigureAutomation(true));
        document.OnClick(".broker-disable", _ => model.DisableAutomation(false));
        document.OnClick(".broker-remove", _ => model.DisableAutomation(true));
        document.OnClick(".broker-forget", _ => model.ForgetPassword());
        document.OnClick(".broker-recover", _ => model.RecoverAutomationSettings());
        document.OnClick(".assign-screen", e =>
        {
            model.OpenAssignment(e.Element.GetAttribute("data-display")); document.Refresh(); ResetScroll(document.Root);
        });
        document.OnClick(".assignment-create", _ => model.SaveAssignment(null));
        document.OnClick(".assignment-reuse", e => model.SaveAssignment(e.Element.GetAttribute("data-assignment")));
        document.OnClick(".detach-screen", e => model.DetachAssignment(e.Element.GetAttribute("data-display")));
        document.OnClick(".settings-recover", _ => model.RecoverSettings());
        document.OnClick(".recovery-setup", _ => model.ConfigureRecovery());
    }
    private static void ResetScroll(CupriFace.Dom.RenderNode node)
    {
        if (node.Element?.ClassList.Contains("content-scroll") == true) node.ScrollY = 0;
        foreach (var child in node.Children) ResetScroll(child);
    }
}

public sealed class ShadeModel : IBindableAccessor
{
    private readonly IDimmingBackend backend;
    private string? error;
    private long errorRevision;
    private IReadOnlyList<Display>? previousDisplays;
    private DisplayModel[] displays = [];
    private MonitorLayout layout = MonitorLayout.Create([]);
    private readonly ScreenControls controls;
    private readonly HomeAssistantIntegration integration;
    private readonly InstanceRequests? requests;
    private readonly Action? shutdown;
    private readonly string windowTitle;
    private long automationPublishedRevision = -1;
    public bool AboutOpen { get; private set; }
    public string AppVersion { get; } = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ShadeApp).Assembly)
        ?.InformationalVersion.Split('+')[0] ?? "Unknown";
    public string ProjectUrl => "https://github.com/Wixely/Shade";
    public bool AutomationOpen { get; private set; }
    public string AutomationStyle => AutomationOpen ? "display:block;" : "display:none;";
    public string ScreensStyle => AutomationOpen || AssignmentOpen ? "display:none;" : "display:block;";
    public string AutomationLabel => AutomationOpen || AssignmentOpen ? "Back to screens" : "Home Assistant";
    public bool AssignmentOpen { get; private set; }
    public string AssignmentStyle => AssignmentOpen ? "display:block;" : "display:none;";
    public string AssignmentName { get; private set; } = "";
    public string AssignmentStatus { get; private set; } = "";
    private string? assignmentId;
    public string AssignmentTarget => backend.Displays.FirstOrDefault(d => d.Id == assignmentId) is { } d
        ? $"{d.Label}: {d.Width} x {d.Height} at ({d.X}, {d.Y})" : "Screen disconnected. Return to screens to select another.";
    public AssignmentChoice[] AvailableAssignments => backend.Assignments.Where(a => !a.Connected).ToArray();
    public void OpenAssignment(string? id)
    {
        assignmentId = id; AssignmentName = ""; AssignmentStatus = ""; AutomationOpen = false; AssignmentOpen = true; uiRevision++;
    }
    public void SaveAssignment(string? existingId)
    {
        try
        {
            var name = existingId is null ? AssignmentName : backend.Assignments.Single(a => a.Id == existingId).Name;
            backend.AssignScreen(assignmentId ?? "", name, existingId);
            AssignmentOpen = false; AssignmentStatus = "";
        }
        catch (AssignmentException ex) { AssignmentStatus = ex.Message; }
        catch (Exception) { AssignmentStatus = "Assignment could not be updated. Check screen connections and settings access."; }
        uiRevision++;
    }
    public void DetachAssignment(string? id)
    {
        if (id is null) return;
        try { backend.DetachAssignment(id); error = null; }
        catch (Exception ex) { ReportError(ex); }
        uiRevision++;
    }
    public string BrokerHost { get; private set; }
    public string BrokerPort { get; private set; }
    public bool BrokerTls { get; private set; }
    public string BrokerUsername { get; private set; }
    public string BrokerPassword { get; private set; } = "";
    public string PasswordHint => integration.HasPassword ? "Password saved for this account. Leave blank to keep it."
        : OperatingSystem.IsLinux() ? "Your desktop keyring protects saved passwords. Unlock it when prompted."
        : "Windows encrypts saved passwords for this account.";
    public string AutomationStatus => integration.Status;
    public string LifecycleHint { get; internal set; } = "Closing Shade removes its overlays.";
    public string AutomationRecoveryStyle => integration.SettingsNeedRecovery ? "" : "display:none";
    public string AutomationRecoveryLabel => integration.SettingsNeedBackup ? "Back up unreadable settings and reset integration" : "Retry saving integration settings";
    public void RecoverAutomationSettings() { _ = integration.RecoverSettingsAsync(); uiRevision++; }
    public void ToggleAutomation() { if (AssignmentOpen) AssignmentOpen = false; else AutomationOpen = !AutomationOpen; uiRevision++; }
    public void ConfigureAutomation(bool enable)
    {
        if (!int.TryParse(BrokerPort, out var port)) port = 0;
        var connection = new BrokerConnection(BrokerHost.Trim(), port, BrokerTls, BrokerUsername, BrokerPassword);
        _ = enable ? integration.ConfigureAsync(connection, true, true) : integration.TestAsync(connection, true);
        // Testing does not save credentials. Keep the masked entry available for Save and enable.
        if (enable) BrokerPassword = "";
        uiRevision++;
    }
    public void DisableAutomation(bool remove) { _ = integration.DisableAsync(remove); uiRevision++; }
    public void ForgetPassword() { BrokerPassword = ""; _ = integration.ForgetPasswordAsync(); uiRevision++; }
    private long uiRevision;
    public bool AdvancedOpen { get; private set; }
    public string AdvancedLabel => AdvancedOpen ? "Advanced  ▴" : "Advanced  ▾";
    public string AdvancedStyle => AdvancedOpen ? "display:block;" : "display:none;";
    public string CanvasStyle { get { _ = Displays; return layout.CanvasStyle; } }
    public ShadeModel(IDimmingBackend backend, HomeAssistantIntegration? integration = null,
        InstanceRequests? requests = null, Action? shutdown = null, string windowTitle = "Shade")
    {
        this.backend = backend;
        controls = new(backend);
        this.integration = integration ?? new();
        this.requests = requests;
        this.shutdown = shutdown;
        this.windowTitle = windowTitle;
        BrokerHost = this.integration.Host; BrokerPort = this.integration.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BrokerTls = this.integration.Tls; BrokerUsername = this.integration.Username;
    }
    public long Revision
    {
        get
        {
            while (integration.TryRead(out var command))
            {
                try { command!.Apply(controls); }
                catch (Exception ex) { ReportError(ex); }
            }
            DrainRequests();
            var current = backend.Revision + controls.Revision;
            if (automationPublishedRevision != current)
            {
                automationPublishedRevision = current;
                _ = Displays; // Use the same cached numbering as the monitor graphic, before filtering untrusted identities.
                integration.Update(new(controls.GlobalLevel, layout.Tiles.Where(t => t.Display.CanRemember).Select(t =>
                {
                    var d = t.Display;
                    var control = controls.Get(d.Id);
                    return new AutomationScreen(d.Id, $"Screen {t.Number}: {d.Label}", control.Enabled, control.UseGlobal, backend.GetLevel(d.Id), d.X, d.Y, d.Width, d.Height);
                }).ToArray(), controls.Maximum));
            }
            return current + uiRevision + integration.Revision;
        }
    }
    public long ObservedRevision { get; private set; } = -1;
    public DisplayModel[] Displays
    {
        get
        {
            var current = backend.Displays;
            if (previousDisplays is null || !previousDisplays.SequenceEqual(current))
            {
                layout = MonitorLayout.Create(current);
                displays = layout.Tiles.Select(t => new DisplayModel(t, backend, controls, ReportError)).ToArray();
                previousDisplays = current;
            }
            return displays;
        }
    }
    public string Status
    {
        get
        {
            return error is not null && errorRevision == backend.Revision ? error : backend.Status;
        }
    }
    public string SettingsRecoveryStyle => backend.SettingsNeedRecovery ? "display:block;" : "display:none;";
    public string RecoverySetupStyle => backend.RecoverySetupNeeded ? "display:block;" : "display:none;";
    public void ConfigureRecovery()
    {
        try { backend.ConfigureRecovery(); error = null; }
        catch (Exception ex) { ReportError(ex); }
        uiRevision++;
    }
    public string SettingsRecoveryLabel => backend.SettingsNeedBackup ? "Back up unreadable file and save current settings" : "Retry saving settings";
    public void RecoverSettings()
    {
        try { backend.RecoverSettings(); error = null; }
        catch (Exception ex) { ReportError(ex); }
        uiRevision++;
    }
    public object? GetBindable(string name) => name switch
    {
        nameof(Displays) => Displays,
        nameof(Status) => Status,
        nameof(RecoverySetupStyle) => RecoverySetupStyle,
        nameof(SettingsRecoveryStyle) => SettingsRecoveryStyle,
        nameof(SettingsRecoveryLabel) => SettingsRecoveryLabel,
        nameof(CanvasStyle) => CanvasStyle,
        nameof(GlobalSummary) => GlobalSummary,
        nameof(GlobalLevel) => GlobalLevel,
        nameof(Maximum) => Maximum,
        nameof(AllowFullShade) => AllowFullShade,
        nameof(GlobalPresetOne) => GlobalPresetOne,
        nameof(GlobalPresetTwo) => GlobalPresetTwo,
        nameof(GlobalPresetThree) => GlobalPresetThree,
        nameof(AdvancedOpen) => AdvancedOpen,
        nameof(AdvancedLabel) => AdvancedLabel,
        nameof(AdvancedStyle) => AdvancedStyle,
        nameof(AboutOpen) => AboutOpen,
        nameof(AppVersion) => AppVersion,
        nameof(ProjectUrl) => ProjectUrl,
        nameof(AutomationOpen) => AutomationOpen,
        nameof(AutomationStyle) => AutomationStyle,
        nameof(ScreensStyle) => ScreensStyle,
        nameof(AutomationLabel) => AutomationLabel,
        nameof(AssignmentStyle) => AssignmentStyle,
        nameof(AssignmentName) => AssignmentName,
        nameof(AssignmentTarget) => AssignmentTarget,
        nameof(AssignmentStatus) => AssignmentStatus,
        nameof(AvailableAssignments) => AvailableAssignments,
        nameof(BrokerHost) => BrokerHost,
        nameof(BrokerPort) => BrokerPort,
        nameof(BrokerTls) => BrokerTls,
        nameof(BrokerUsername) => BrokerUsername,
        nameof(BrokerPassword) => BrokerPassword,
        nameof(PasswordHint) => PasswordHint,
        nameof(AutomationStatus) => AutomationStatus,
        nameof(LifecycleHint) => LifecycleHint,
        nameof(AutomationRecoveryStyle) => AutomationRecoveryStyle,
        nameof(AutomationRecoveryLabel) => AutomationRecoveryLabel,
        _ => name.StartsWith("Level_", StringComparison.Ordinal) ? backend.GetLevel(name[6..])
            : name.StartsWith("UseGlobal_", StringComparison.Ordinal) ? controls.Get(name[10..]).UseGlobal : null
    };
    public bool SetBindable(string name, object? value)
    {
        try
        {
            switch (name)
            {
                case nameof(AboutOpen): AboutOpen = Convert.ToBoolean(value); uiRevision++; return true;
                case nameof(AssignmentName): AssignmentName = Convert.ToString(value) ?? ""; uiRevision++; return true;
                case nameof(AllowFullShade): controls.SetAllowFullShade(Convert.ToBoolean(value)); uiRevision++; return true;
                case nameof(BrokerHost): BrokerHost = Convert.ToString(value) ?? ""; uiRevision++; return true;
                case nameof(BrokerPort): BrokerPort = Convert.ToString(value) ?? ""; uiRevision++; return true;
                case nameof(BrokerTls):
                    var tls = Convert.ToBoolean(value);
                    if (tls == BrokerTls) return true;
                    if (tls && BrokerPort == "1883") BrokerPort = "8883";
                    else if (!tls && BrokerPort == "8883") BrokerPort = "1883";
                    BrokerTls = tls;
                    uiRevision++;
                    return true;
                case nameof(BrokerUsername): BrokerUsername = Convert.ToString(value) ?? ""; uiRevision++; return true;
                case nameof(BrokerPassword): BrokerPassword = Convert.ToString(value) ?? ""; uiRevision++; return true;
            }
            if (name == nameof(GlobalLevel)) controls.SetGlobal(Convert.ToInt32(value));
            else if (name.StartsWith("UseGlobal_", StringComparison.Ordinal)) controls.SetUseGlobal(name[10..], Convert.ToBoolean(value));
            else if (name.StartsWith("Level_", StringComparison.Ordinal)) controls.SetIndividual(name[6..], Convert.ToInt32(value));
            else return false;
            error = null; return true;
        }
        catch (Exception ex) { ReportError(ex); return false; }
    }
    public int GlobalLevel => controls.GlobalLevel;
    public int Maximum => controls.Maximum;
    public bool AllowFullShade => controls.AllowFullShade;
    public int GlobalPresetOne => AllowFullShade ? 25 : 20;
    public int GlobalPresetTwo => AllowFullShade ? 50 : 40;
    public int GlobalPresetThree => AllowFullShade ? 75 : 60;
    public string GlobalSummary
    {
        get
        {
            // First binding in the template: capture before reading state for this frame.
            ObservedRevision = Revision;
            return $"{GlobalLevel}% dimming";
        }
    }
    public void SetGlobal(int level)
    {
        try
        {
            controls.SetGlobal(level);
            error = null;
        }
        catch (Exception ex) { ReportError(ex); }
    }
    public void Toggle(string? id)
    {
        if (id is null) return;
        try
        {
            controls.Toggle(id);
            error = null;
        }
        catch (Exception ex) { ReportError(ex); }
    }
    public void ToggleAdvanced() { AdvancedOpen = !AdvancedOpen; uiRevision++; }
    private void ReportError(Exception ex)
    {
        errorRevision = backend.Revision;
        error = ex is ArgumentException
            ? "This display changed or the level was invalid. Controls update automatically."
            : backend.Status;
    }
    public void RestoreAll()
    {
        try
        {
            controls.RestoreAll(); error = null;
        }
        catch (Exception ex) { ReportError(ex); }
    }
    public void Restore(string? id)
    {
        if (id is null) return;
        try { controls.Disable(id); error = null; }
        catch (Exception ex) { ReportError(ex); }
    }

    // A second launch forwards its command line instead of opening a window. Those requests are
    // applied here, on the interface thread that owns every control, through the same paths the
    // on-screen controls use, so a live window updates exactly as if the user had clicked.
    private void DrainRequests()
    {
        if (requests is null) return;
        while (requests.TryRead(out var request)) request!.Complete(Apply(request.Operations));
        if (requests.TryClaimShutdown()) shutdown?.Invoke();
    }

    private InstanceResult Apply(IReadOnlyList<ShadeOperation> operations)
    {
        _ = Displays; // Refresh the cached layout so screen numbers match the ones on screen.
        var tiles = layout.Tiles;
        // Resolve every screen reference first: an unknown number rejects the whole request rather
        // than applying the part of it that happened to come earlier.
        foreach (var operation in operations)
        {
            if (operation.Verb is not (ShadeVerb.Level or ShadeVerb.Enabled or ShadeVerb.Link)) continue;
            if (operation.Screen != ShadeOperation.AllScreens && tiles.All(t => t.Number != operation.Screen))
                return InstanceResult.Rejected(tiles.Count == 0 ? "No screens are available."
                    : $"Screen {operation.Screen} does not exist. Screens 1 to {tiles.Count} are connected.");
        }
        var lines = new List<string>();
        try
        {
            foreach (var operation in operations)
                if (Apply(operation, tiles, lines) is { } rejected) return rejected;
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return InstanceResult.Rejected(ex is ArgumentOutOfRangeException
                ? $"That level needs 100% shading enabled; without it the highest level is {DimLevel.DefaultMaximum}%."
                : "Shade could not apply the request. Screens may have changed.");
        }
        error = null;
        uiRevision++;
        return InstanceResult.Accepted(lines, operations.Any(o => o.Verb == ShadeVerb.Exit));
    }

    private InstanceResult? Apply(ShadeOperation operation, IReadOnlyList<MonitorTile> tiles, List<string> lines)
    {
        IEnumerable<string> Targets() => operation.Screen == ShadeOperation.AllScreens
            ? tiles.Select(t => t.Display.Id)
            : tiles.Where(t => t.Number == operation.Screen).Select(t => t.Display.Id);
        var on = operation.Value != 0;
        switch (operation.Verb)
        {
            case ShadeVerb.Global: controls.SetGlobal(operation.Value); break;
            case ShadeVerb.Level: foreach (var id in Targets()) controls.SetIndividual(id, operation.Value); break;
            case ShadeVerb.Enabled:
                foreach (var id in Targets()) if (controls.Get(id).Enabled != on) controls.Toggle(id);
                break;
            case ShadeVerb.Link: foreach (var id in Targets()) controls.SetUseGlobal(id, on); break;
            case ShadeVerb.FullShade: controls.SetAllowFullShade(on); break;
            case ShadeVerb.Restore: controls.RestoreAll(); break;
            case ShadeVerb.Advanced: AdvancedOpen = on; break;
            case ShadeVerb.HomeAssistant: return Automation(on);
            case ShadeVerb.Activate:
            case ShadeVerb.Hide:
                if (!OperatingSystem.IsWindows()) lines.Add("This desktop cannot show or hide the control window on request.");
                else if (operation.Verb == ShadeVerb.Activate)
                { if (!WindowActivation.Raise(windowTitle)) lines.Add("The control window could not be brought forward."); }
                else if (!WindowActivation.Conceal(windowTitle)) lines.Add("The control window could not be hidden.");
                break;
            case ShadeVerb.Status: Report(tiles, lines); break;
            case ShadeVerb.Exit: break; // Applied by the channel once this reply has been delivered.
        }
        return null;
    }

    private InstanceResult? Automation(bool enable)
    {
        if (!enable) { DisableAutomation(false); return null; }
        if (integration.Host.Trim().Length == 0)
            return InstanceResult.Rejected("Home Assistant has no saved broker. Set one in Shade first.");
        // Reuse the saved broker and its protected password. Credentials never cross a command line,
        // because other programs on this computer can read one.
        _ = integration.ConfigureAsync(new(integration.Host, integration.Port, integration.Tls, integration.Username, ""), true, true);
        uiRevision++;
        return null;
    }

    private void Report(IReadOnlyList<MonitorTile> tiles, List<string> lines)
    {
        string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lines.Add("version " + AppVersion);
        lines.Add("global " + Number(GlobalLevel));
        lines.Add("maximum " + Number(Maximum));
        lines.Add("full-shade " + (AllowFullShade ? "on" : "off"));
        lines.Add("home-assistant " + (integration.Enabled ? "on" : "off"));
        lines.Add("advanced " + (AdvancedOpen ? "on" : "off"));
        foreach (var tile in tiles)
        {
            var control = controls.Get(tile.Display.Id);
            lines.Add(string.Join(' ', "screen", Number(tile.Number), control.Enabled ? "on" : "off",
                "level", Number(backend.GetLevel(tile.Display.Id)),
                control.UseGlobal ? "global" : "independent", InstanceChannel.Single(tile.Display.Label)));
        }
    }
}

public sealed class DisplayModel(MonitorTile tile, IDimmingBackend backend, ScreenControls controls, Action<Exception> reportError) : IBindableAccessor
{
    private Display display => tile.Display;
    public int Number => tile.Number;
    public int Maximum => controls.Maximum;
    public string TileStyle => tile.Style;
    public bool Enabled => controls.Get(Id).Enabled;
    public bool UseGlobal => controls.Get(Id).UseGlobal;
    public string GlobalBindingPath => "UseGlobal_" + Id;
    public string GlobalLabel => $"Use global for screen {Number}";
    public string ModeLabel => UseGlobal ? "Follows global when enabled" : "Independent level";
    public string TileState => Enabled ? "on" : "off";
    public string StateLabel => Enabled ? $"On · {Level}%" : "Off";
    public string ToggleLabel => $"Screen {Number}: {Label}, dimming {(Enabled ? "on" : "off")}";
    public string Id => display.Id;
    public string Label => display.Label;
    public string SliderLabel => $"Dimming for {Label}";
    public string BindingPath => "Level_" + Id;
    public string IdentityNote => display.IdentityNote;
    public string AssignStyle => backend.SupportsAssignments && !display.CanRemember ? "display:block;" : "display:none;";
    public string DetachStyle => backend.SupportsAssignments && Id.StartsWith("assigned-v1-", StringComparison.Ordinal) ? "display:block;" : "display:none;";
    public string Geometry => $"{display.Width} x {display.Height} at ({display.X}, {display.Y})";
    public int Level
    {
        get => backend.GetLevel(Id);
        set
        {
            try { controls.SetIndividual(Id, DimLevel.Validate(value)); }
            catch (Exception ex) { reportError(ex); }
        }
    }
    public object? GetBindable(string name) => name switch
    {
        nameof(Id) => Id,
        nameof(Number) => Number,
        nameof(Maximum) => Maximum,
        nameof(TileStyle) => TileStyle,
        nameof(Enabled) => Enabled,
        nameof(UseGlobal) => UseGlobal,
        nameof(GlobalBindingPath) => GlobalBindingPath,
        nameof(GlobalLabel) => GlobalLabel,
        nameof(ModeLabel) => ModeLabel,
        nameof(TileState) => TileState,
        nameof(StateLabel) => StateLabel,
        nameof(ToggleLabel) => ToggleLabel,
        nameof(Label) => Label,
        nameof(SliderLabel) => SliderLabel,
        nameof(BindingPath) => BindingPath,
        nameof(IdentityNote) => IdentityNote,
        nameof(AssignStyle) => AssignStyle,
        nameof(DetachStyle) => DetachStyle,
        nameof(Geometry) => Geometry,
        nameof(Level) => Level,
        _ => null
    };
    public bool SetBindable(string name, object? value)
    {
        if (name != nameof(Level)) return false;
        Level = Convert.ToInt32(value); return true;
    }
}
