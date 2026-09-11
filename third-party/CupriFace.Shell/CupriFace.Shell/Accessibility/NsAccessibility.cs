using CupriFace.Accessibility;

namespace CupriFace.Shell.Accessibility;

/// <summary>
/// NSAccessibility vocabulary: the maps from the engine's ARIA-flavoured semantics to the AX roles,
/// subroles and actions a Mac screen reader understands. Pure data and pure functions — no interop,
/// so this half of the bridge is readable (and reviewable) on any OS.
///
/// Kept deliberately parallel to <see cref="AtSpi"/> and <c>UiaBridge.ControlTypeOf</c>: a control
/// that reads correctly to Narrator and Orca should read correctly to VoiceOver, and three maps
/// that drifted apart would be three different products.
/// </summary>
internal static class NsAccessibility
{
    // Roles (NSAccessibilityRole constants are just these strings).
    internal const string RoleButton = "AXButton";
    internal const string RoleCheckBox = "AXCheckBox";
    internal const string RoleRadioButton = "AXRadioButton";
    internal const string RoleSlider = "AXSlider";
    internal const string RoleProgressIndicator = "AXProgressIndicator";
    internal const string RoleIncrementor = "AXIncrementor";
    internal const string RoleTextField = "AXTextField";
    internal const string RoleComboBox = "AXComboBox";
    internal const string RoleList = "AXList";
    internal const string RoleTabGroup = "AXTabGroup";
    internal const string RoleGroup = "AXGroup";
    internal const string RoleStaticText = "AXStaticText";
    internal const string RoleImage = "AXImage";
    internal const string RoleLink = "AXLink";
    internal const string RoleHeading = "AXHeading";
    internal const string RoleToolbar = "AXToolbar";
    internal const string RoleMenu = "AXMenu";
    internal const string RoleMenuBar = "AXMenuBar";
    internal const string RoleMenuItem = "AXMenuItem";
    internal const string RoleTable = "AXTable";
    internal const string RoleRow = "AXRow";
    internal const string RoleCell = "AXCell";
    internal const string RoleColumn = "AXColumn";
    internal const string RoleOutline = "AXOutline";
    internal const string RoleSheet = "AXSheet";
    internal const string RoleSplitter = "AXSplitter";
    internal const string RoleUnknown = "AXUnknown";

    // Subroles refine a role where macOS has no distinct one of its own.
    internal const string SubroleSwitch = "AXSwitch";
    internal const string SubroleSecureTextField = "AXSecureTextField";

    /// <summary>ARIA role → AX role.</summary>
    /// <remarks>Two mappings look wrong and are not. A <c>tab</c> is an <c>AXRadioButton</c> inside
    /// an <c>AXTabGroup</c> — that is genuinely how Cocoa models tabs, and VoiceOver announces it
    /// correctly. A <c>switch</c> is an <c>AXCheckBox</c> carrying the <c>AXSwitch</c> subrole,
    /// because macOS has no top-level switch role.</remarks>
    internal static string RoleOf(string role) => role switch
    {
        "button" => RoleButton,
        "link" => RoleLink,
        "checkbox" or "switch" => RoleCheckBox,
        "radio" or "tab" => RoleRadioButton,
        "radiogroup" => RoleGroup,
        "slider" => RoleSlider,
        "progressbar" => RoleProgressIndicator,
        "spinbutton" => RoleIncrementor,
        "textbox" => RoleTextField,
        "combobox" => RoleComboBox,
        "listbox" or "list" => RoleList,
        "option" or "listitem" => RoleStaticText,
        "tablist" => RoleTabGroup,
        "tabpanel" or "navigation" or "group" or "document" => RoleGroup,
        "row" => RoleRow,
        "tree" => RoleOutline,
        "treeitem" => RoleRow,
        "heading" => RoleHeading,
        "status" or "alert" or "text" or "label" or "tooltip" => RoleStaticText,
        "image" or "img" => RoleImage,
        "menu" => RoleMenu,
        "menubar" => RoleMenuBar,
        "menuitem" => RoleMenuItem,
        "dialog" or "alertdialog" => RoleSheet,
        "table" => RoleTable,
        "columnheader" => RoleColumn,
        "cell" => RoleCell,
        "separator" => RoleSplitter,
        "toolbar" => RoleToolbar,
        _ => RoleGroup,
    };

    /// <summary>The subrole, or null when the role needs no refinement.</summary>
    internal static string? SubroleOf(AccessibilityNode n) => n.Role switch
    {
        "switch" => SubroleSwitch,
        _ => null,
    };

    /// <summary>True when VoiceOver should treat the node as a leaf it can land on. Containers that
    /// exist only to group children answer false, or the user tabs through scaffolding — and so does
    /// anything off screen, which macOS then splices out of its parent's children entirely. (UIA and
    /// AT-SPI keep off-screen nodes in the tree and flag them; on macOS the idiomatic equivalent is
    /// to stop being an element, which is what element hiding is for.)</summary>
    internal static bool IsElement(AccessibilityNode n) =>
        !n.Offscreen &&
        n.Role is not ("group" or "document" or "navigation" or "tabpanel" or "radiogroup");

    /// <summary>The single action a node exposes, as an AX action name — the counterpart of UIA's
    /// Invoke and AT-SPI's DoAction. Null means the node advertises none.</summary>
    internal static string? ActionOf(AccessibilityNode n) => n.Role switch
    {
        "button" or "link" or "menuitem" or "tab" or "option" or "listitem" or "treeitem"
            or "checkbox" or "switch" or "radio" => "AXPress",
        _ => null,
    };

    /// <summary>True when the node carries a numeric range the Value attributes should serve.</summary>
    internal static bool HasValue(AccessibilityNode n) =>
        n.Role is "slider" or "progressbar" or "spinbutton" && n.Now is not null;

    /// <summary>What <c>AXValue</c> should answer. Checkables report 1/0 (VoiceOver says "checked"
    /// off that, not off the role), ranges report their number, and everything else its text.</summary>
    internal static object? ValueOf(AccessibilityNode n)
    {
        if (n.Checked is { } isChecked) return isChecked ? 1.0 : 0.0;
        if (n.Selected is { } isSelected && n.Role is "tab" or "option" or "listitem" or "treeitem")
            return isSelected ? 1.0 : 0.0;
        if (HasValue(n)) return n.Now!.Value;
        return n.Value;
    }
}
