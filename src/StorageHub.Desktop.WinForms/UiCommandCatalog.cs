namespace StorageHub.Desktop;

public sealed record UiCommandDefinition(
    string Id,
    string Menu,
    string Label,
    string Description,
    Keys Shortcut = Keys.None,
    UiGlyph? Glyph = null);

public static class UiCommandCatalog
{
    public static IReadOnlyList<string> TopMenus { get; } =
    [
        "Workspace",
        "Edit",
        "View",
        "Go",
        "Connections",
        "Transfer",
        "Sync",
        "Tools",
        "Help"
    ];

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Commands { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Workspace"] = ["New Workspace...", "Open Workspace...", "Save Workspace", "Save Workspace As...", "Rename Workspace...", "Close Workspace", "Exit"],
            ["Edit"] = ["New Folder", "New Empty File...", "Cut", "Copy", "Paste", "Rename", "Batch Rename...", "Delete", "Select All", "Invert Selection", "Properties"],
            ["View"] = ["Refresh", "Directory Tree", "Transfer Queue", "Session Log", "Hidden Files", "Theme"],
            ["Go"] = ["Back", "Forward", "Up", "Home", "History", "Favorites"],
            ["Connections"] = ["Connection Manager...", "Quick Connect...", "Reconnect", "Disconnect", "Test Connection"],
            ["Transfer"] = ["Start Queue", "Pause All", "Resume All", "Cancel Selected", "Speed Limits..."],
            ["Sync"] = ["Compare Panes", "Review & Run...", "Sync Profiles...", "Schedules..."],
            ["Tools"] = ["Search...", "Checksums...", "Settings...", "Logs...", "Diagnostics..."],
            ["Help"] = ["Check for Updates...", "Keyboard Shortcuts", "Documentation", "Report Issue", "About StorageHub"]
        };

    private static readonly IReadOnlyDictionary<string, (Keys Shortcut, UiGlyph? Glyph, string Description)> Metadata =
        new Dictionary<string, (Keys, UiGlyph?, string)>(StringComparer.Ordinal)
        {
            ["New Workspace..."] = (Keys.Control | Keys.T, UiGlyph.Add, "Choose a one- to four-pane workspace."),
            ["Open Workspace..."] = (Keys.Control | Keys.O, UiGlyph.Folder, "Open a saved StorageHub workspace file."),
            ["Save Workspace"] = (Keys.Control | Keys.S, UiGlyph.Save, "Save the active workspace."),
            ["Save Workspace As..."] = (Keys.Control | Keys.Shift | Keys.S, UiGlyph.Save, "Save the active workspace to a new file."),
            ["Rename Workspace..."] = (Keys.None, null, "Rename the active workspace tab."),
            ["Close Workspace"] = (Keys.Control | Keys.W, UiGlyph.Delete, "Close the active workspace tab."),
            ["Cut"] = (Keys.Control | Keys.X, UiGlyph.Forward, "Stage selected files for moving to another pane."),
            ["Copy"] = (Keys.Control | Keys.C, UiGlyph.File, "Stage selected files for copying to another pane."),
            ["Paste"] = (Keys.Control | Keys.V, UiGlyph.Save, "Enqueue the staged operation in this pane."),
            ["New Folder"] = (Keys.Control | Keys.Shift | Keys.N, UiGlyph.Folder, "Create a folder in the active pane."),
            ["New Empty File..."] = (Keys.None, UiGlyph.File, "Create an empty file in the active pane."),
            ["Rename"] = (Keys.F2, null, "Rename the focused item."),
            ["Batch Rename..."] = (Keys.None, null, "Preview and rename several selected items."),
            ["Delete"] = (Keys.Delete, UiGlyph.Delete, "Review and delete the selected items."),
            ["Invert Selection"] = (Keys.Control | Keys.I, UiGlyph.Test, "Invert the visible selection in the active pane."),
            ["Properties"] = (Keys.Alt | Keys.Enter, null, "Inspect read-only versions, metadata, and tags for one saved-connection file."),
            ["Select All"] = (Keys.Control | Keys.A, UiGlyph.Test, "Select every visible item."),
            ["Refresh"] = (Keys.F5, UiGlyph.Refresh, "Refresh the focused pane."),
            ["Back"] = (Keys.Alt | Keys.Left, UiGlyph.Back, "Return to the previous location."),
            ["Forward"] = (Keys.Alt | Keys.Right, UiGlyph.Forward, "Move to the next location in history."),
            ["Up"] = (Keys.Alt | Keys.Up, UiGlyph.Up, "Open the parent location."),
            ["Connection Manager..."] = (Keys.Control | Keys.Shift | Keys.M, UiGlyph.Connections, "Create, organize, and test connection profiles."),
            ["Quick Connect..."] = (Keys.Control | Keys.K, UiGlyph.Connections, "Open a temporary connection without saving credentials in the profile."),
            ["Start Queue"] = (Keys.F7, UiGlyph.Run, "Start queued transfers."),
            ["Pause All"] = (Keys.F8, UiGlyph.Pause, "Pause active transfers at safe checkpoints."),
            ["Compare Panes"] = (Keys.Control | Keys.D, UiGlyph.Compare, "Compare the visible source and destination."),
            ["Review & Run..."] = (Keys.Control | Keys.Shift | Keys.P, UiGlyph.Compare, "Review an exact sync plan, then run it when its safety checks pass."),
            ["Schedules..."] = (Keys.Control | Keys.Shift | Keys.S, UiGlyph.Run, "Manage durable review-only or safety-gated automatic synchronization schedules."),
            ["Search..."] = (Keys.Control | Keys.F, UiGlyph.Search, "Search within the focused endpoint."),
            ["Settings..."] = (Keys.Control | Keys.Oemcomma, UiGlyph.Settings, "Configure automatic StorageHub updates."),
            ["Check for Updates..."] = (Keys.None, UiGlyph.Refresh, "Check the official StorageHub GitHub releases for an update."),
            ["About StorageHub"] = (Keys.None, UiGlyph.Info, "Show StorageHub version and application information.")
        };

    private static readonly IReadOnlyList<UiCommandDefinition> CommandDefinitions = BuildDefinitions();

    public static IReadOnlyList<UiCommandDefinition> Definitions => CommandDefinitions;

    public static UiCommandDefinition GetDefinition(string menu, string label) =>
        CommandDefinitions.First(definition =>
            string.Equals(definition.Menu, menu, StringComparison.Ordinal) &&
            string.Equals(definition.Label, label, StringComparison.Ordinal));

    private static System.Collections.ObjectModel.ReadOnlyCollection<UiCommandDefinition> BuildDefinitions()
    {
        var definitions = new List<UiCommandDefinition>();
        foreach (var menu in TopMenus)
        {
            foreach (var label in Commands[menu])
            {
                var id = $"{menu}.{label}"
                    .Replace("...", string.Empty, StringComparison.Ordinal)
                    .Replace(' ', '-')
                    .ToLowerInvariant();
                var metadata = Metadata.GetValueOrDefault(label, (Keys.None, null, $"Run {label.TrimEnd('.').ToLowerInvariant()}."));
                definitions.Add(new UiCommandDefinition(id, menu, label, metadata.Description, metadata.Shortcut, metadata.Glyph));
            }
        }

        return definitions.AsReadOnly();
    }
}
