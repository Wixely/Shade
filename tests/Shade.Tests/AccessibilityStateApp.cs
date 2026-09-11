using CupriFace;
using CupriFace.Binding;

// Isolated host fixture: no Shade backend, settings, broker or display overlays.
internal sealed class AccessibilityStateApp : CupriApp, IBindableAccessor
{
    private string value = "initial";
    private string state = "editable";
    private string secret = "";
    public override string Title => "Shade accessibility state fixture";
    public override int Width => 620;
    public override int Height => 500;
    public override object Model => this;
    public override CupriFace.Components.ComponentRegistry Components => base.Components.Register(new Shade.AccessiblePasswordComponent());
    public override string Html => """
        <html><body>
        <p>State: {{State}}</p>
        <cupri-textfield value="{{Value}}" placeholder="Enter a value" aria-label="Fixture value"
            aria-readonly="{{ReadOnly}}" aria-disabled="{{Disabled}}"></cupri-textfield>
        <button role="button" id="editable">Editable</button>
        <button role="button" id="readonly">Read only</button>
        <button role="button" id="disabled">Disabled</button>
        <cupri-password value="{{Secret}}" aria-label="Fixture password"></cupri-password>
        <p role="status" aria-label="{{SecretStatus}}">{{SecretStatus}}</p>
        </body></html>
        """;
    public override string Css => """
        body { padding:24px; background:white; color:black; }
        button { display:block; padding:10px; margin-top:10px; }
        """;
    public override void Configure(CupriDocument document)
    {
        foreach (var mode in new[] { "editable", "readonly", "disabled" })
            document.OnClick("#" + mode, _ => { state = mode; document.Refresh(); });
    }
    public object? GetBindable(string name) => name switch
    {
        "Value" => value, "State" => state, "Secret" => secret,
        "SecretStatus" => secret == "synthetic-event-password" ? "Secret updated" : "Secret pending",
        "ReadOnly" => state == "readonly" ? "true" : "false",
        "Disabled" => state == "disabled" ? "true" : "false", _ => null
    };
    public bool SetBindable(string name, object? replacement)
    {
        if (name == "Secret") { secret = Convert.ToString(replacement) ?? ""; return true; }
        if (name != "Value") return false;
        value = Convert.ToString(replacement) ?? "";
        return true;
    }
}
