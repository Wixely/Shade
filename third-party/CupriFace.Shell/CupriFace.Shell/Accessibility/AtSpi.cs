using CupriFace.Accessibility;

namespace CupriFace.Shell.Accessibility;

/// <summary>
/// AT-SPI2 vocabulary: bus/interface names, and the maps from the engine's ARIA-flavoured
/// semantics to AT-SPI's role and state enums. Pure data and pure functions — no D-Bus, no
/// interop, testable on any OS (which is how the maps are covered without a Linux machine).
/// </summary>
internal static class AtSpi
{
    internal const string BusName = "org.a11y.Bus";
    internal const string BusPath = "/org/a11y/bus";

    internal const string RegistryName = "org.a11y.atspi.Registry";
    internal const string RootPath = "/org/a11y/atspi/accessible/root";
    internal const string NullPath = "/org/a11y/atspi/null";
    internal const string PathPrefix = "/org/a11y/atspi/accessible/";
    internal const string CachePath = "/org/a11y/atspi/cache";
    /// <summary>Everything we serve hangs off here, so ONE subtree handler covers the accessible
    /// objects and the cache alike.</summary>
    internal const string TreeRoot = "/org/a11y/atspi";

    internal const string IfaceAccessible = "org.a11y.atspi.Accessible";
    internal const string IfaceApplication = "org.a11y.atspi.Application";
    internal const string IfaceComponent = "org.a11y.atspi.Component";
    internal const string IfaceAction = "org.a11y.atspi.Action";
    internal const string IfaceValue = "org.a11y.atspi.Value";
    internal const string IfaceSocket = "org.a11y.atspi.Socket";
    internal const string IfaceCache = "org.a11y.atspi.Cache";
    internal const string IfaceProperties = "org.freedesktop.DBus.Properties";
    internal const string IfaceIntrospectable = "org.freedesktop.DBus.Introspectable";

    internal const string EventObject = "org.a11y.atspi.Event.Object";
    internal const string EventFocus = "org.a11y.atspi.Event.Focus";

    // ---- roles (AtspiRole, atspi-constants.h) -------------------------------------------------
    internal const uint RoleInvalid = 0;
    internal const uint RoleCheckBox = 7;
    internal const uint RoleComboBox = 11;
    internal const uint RoleDialog = 16;
    internal const uint RoleFrame = 23;
    internal const uint RoleLabel = 29;
    internal const uint RoleList = 31;
    internal const uint RoleListItem = 32;
    internal const uint RoleMenu = 33;
    internal const uint RoleMenuBar = 34;
    internal const uint RoleMenuItem = 35;
    internal const uint RolePageTab = 37;
    internal const uint RolePageTabList = 38;
    internal const uint RolePanel = 39;
    internal const uint RolePasswordText = 40;
    internal const uint RoleProgressBar = 42;
    internal const uint RolePushButton = 43;
    internal const uint RoleRadioButton = 44;
    internal const uint RoleSeparator = 50;
    internal const uint RoleSlider = 51;
    internal const uint RoleSpinButton = 52;
    internal const uint RoleTable = 55;
    internal const uint RoleTableCell = 56;
    internal const uint RoleTableColumnHeader = 57;
    internal const uint RoleToggleButton = 62;
    internal const uint RoleToolBar = 63;
    internal const uint RoleToolTip = 64;
    internal const uint RoleTree = 65;
    internal const uint RoleUnknown = 67;
    internal const uint RoleApplication = 75;
    internal const uint RoleEntry = 79;
    internal const uint RoleImage = 27;
    internal const uint RoleHeading = 83;
    internal const uint RoleSection = 85;
    internal const uint RoleLink = 88;
    internal const uint RoleTreeItem = 90;
    internal const uint RoleStatusBar = 54;
    internal const uint RoleGrouping = 39;   // AT-SPI has no "group"; PANEL is the conventional stand-in

    /// <summary>ARIA role → AT-SPI role. The Linux twin of <c>UiaBridge.ControlTypeOf</c>, kept
    /// deliberately parallel so a control that reads correctly to Narrator reads correctly to
    /// Orca.</summary>
    internal static uint RoleOf(string role) => role switch
    {
        "button" => RolePushButton,
        "link" => RoleLink,
        "checkbox" => RoleCheckBox,
        "switch" => RoleToggleButton,          // a switch IS a toggle button to AT-SPI
        "radio" => RoleRadioButton,
        "radiogroup" => RolePanel,
        "slider" => RoleSlider,
        "progressbar" => RoleProgressBar,
        "spinbutton" => RoleSpinButton,
        "textbox" => RoleEntry,
        "combobox" => RoleComboBox,
        "listbox" or "list" => RoleList,
        "option" or "listitem" => RoleListItem,
        "tablist" => RolePageTabList,
        "tab" => RolePageTab,
        "tabpanel" or "navigation" => RolePanel,
        "tree" => RoleTree,
        "treeitem" => RoleTreeItem,
        "group" => RoleGrouping,
        "heading" => RoleHeading,
        "status" or "alert" => RoleStatusBar,
        "image" or "img" => RoleImage,
        "menu" => RoleMenu,
        "menubar" => RoleMenuBar,
        "menuitem" => RoleMenuItem,
        "dialog" or "alertdialog" => RoleDialog,
        "tooltip" => RoleToolTip,
        "table" => RoleTable,
        "row" => RolePanel,
        "columnheader" => RoleTableColumnHeader,
        "cell" => RoleTableCell,
        "separator" => RoleSeparator,
        "document" => RoleFrame,
        "toolbar" => RoleToolBar,
        "text" or "label" => RoleLabel,
        _ => RoleSection,
    };

    /// <summary>The role name AT clients read aloud when they don't localise the enum.</summary>
    internal static string RoleNameOf(uint role) => role switch
    {
        RolePushButton => "push button",
        RoleLink => "link",
        RoleCheckBox => "check box",
        RoleToggleButton => "toggle button",
        RoleRadioButton => "radio button",
        RoleSlider => "slider",
        RoleProgressBar => "progress bar",
        RoleSpinButton => "spin button",
        RoleEntry => "entry",
        RolePasswordText => "password text",
        RoleComboBox => "combo box",
        RoleList => "list",
        RoleListItem => "list item",
        RolePageTabList => "page tab list",
        RolePageTab => "page tab",
        RoleTree => "tree",
        RoleTreeItem => "tree item",
        RoleHeading => "heading",
        RoleImage => "image",
        RoleMenu => "menu",
        RoleMenuBar => "menu bar",
        RoleMenuItem => "menu item",
        RoleDialog => "dialog",
        RoleToolTip => "tool tip",
        RoleTable => "table",
        RoleTableCell => "table cell",
        RoleTableColumnHeader => "table column header",
        RoleSeparator => "separator",
        RoleFrame => "frame",
        RoleToolBar => "tool bar",
        RoleLabel => "label",
        RoleStatusBar => "statusbar",
        RoleApplication => "application",
        RolePanel => "panel",
        RoleSection => "section",
        _ => "unknown",
    };

    // ---- states (AtspiStateType) --------------------------------------------------------------
    private const int StateEditable = 7;
    private const int StateEnabled = 8;
    private const int StateExpandable = 9;
    private const int StateExpanded = 10;
    private const int StateFocusable = 11;
    private const int StateFocused = 12;
    private const int StateChecked = 4;
    private const int StateSelectable = 22;
    private const int StateSelected = 23;
    private const int StateSensitive = 24;
    private const int StateShowing = 25;
    private const int StateVisible = 30;

    /// <summary>The states worth a signal when they flip between two frames. An AT learns the
    /// initial state from the cache, but it only learns about a CHANGE from an event — a checkbox
    /// that never emits this is one a screen-reader user ticks and hears nothing about.
    /// "focused" is absent on purpose: the bridge emits it around the focus move itself, where it
    /// can also announce the node that LOST focus (which a both-frames diff would miss).</summary>
    internal static readonly (int Bit, string Name)[] NotifiedStates =
    [
        (StateChecked, "checked"),
        (StateEnabled, "enabled"),
        (StateSensitive, "sensitive"),
        (StateSelected, "selected"),
        (StateExpanded, "expanded"),
        (StateShowing, "showing"),
        (StateVisible, "visible"),
    ];

    /// <summary>The AT-SPI state set as one 64-bit mask.</summary>
    internal static ulong StateBitsOf(AccessibilityNode n)
    {
        ulong bits = 0;
        void Set(int state) => bits |= 1UL << state;

        // AT-SPI separates the two, and the difference is exactly what off-screen means here:
        // VISIBLE is "would be rendered if you scrolled to it", SHOWING is "is on screen now".
        // Orca skips anything not SHOWING, which is how a reader stays on the visible page.
        Set(StateVisible);
        if (!n.Offscreen) Set(StateShowing);
        if (!n.Disabled) { Set(StateEnabled); Set(StateSensitive); }
        if (n.Focusable) Set(StateFocusable);
        if (n.Focused) Set(StateFocused);
        if (n.Checked == true) Set(StateChecked);
        if (n.Selected is not null) Set(StateSelectable);
        if (n.Selected == true) Set(StateSelected);
        if (n.Expanded is not null) Set(StateExpandable);
        if (n.Expanded == true) Set(StateExpanded);
        if (n.Role is "textbox" or "combobox" && !n.Disabled) Set(StateEditable);

        return bits;
    }

    /// <summary>The same set in its wire form: two uint32s, low bits first. Anything an AT asks
    /// ("is this checked / focused / enabled") answers from here, so the mapping is the difference
    /// between a control that announces its state and one that stays silent about it.</summary>
    internal static (uint Low, uint High) StatesOf(AccessibilityNode n)
    {
        var bits = StateBitsOf(n);
        return ((uint)(bits & 0xFFFFFFFF), (uint)(bits >> 32));
    }

    /// <summary>The action a node exposes, if any — the AT-SPI counterpart of UIA's Invoke
    /// pattern. Null means the node advertises no actions at all.</summary>
    internal static string? ActionNameOf(AccessibilityNode n) => n.Role switch
    {
        "button" or "link" or "menuitem" or "tab" or "option" or "listitem" or "treeitem" => "click",
        "checkbox" or "switch" or "radio" => "toggle",
        _ => null,
    };

    /// <summary>True when the node carries a numeric range AT-SPI's Value interface should serve.</summary>
    internal static bool HasValue(AccessibilityNode n) =>
        n.Role is "slider" or "progressbar" or "spinbutton" && n.Now is not null;
}
