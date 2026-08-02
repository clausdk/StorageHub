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
        "File",
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
            ["File"] = ["New Workspace Tab", "Close Tab", "Import Profiles...", "Export Profiles...", "Exit"],
            ["Edit"] = ["Cut", "Copy", "Paste", "Rename", "Batch Rename...", "Select All", "Invert Selection", "Properties"],
            ["View"] = ["Refresh", "Directory Tree", "Transfer Queue", "Session Log", "Hidden Files", "Theme"],
            ["Go"] = ["Back", "Forward", "Up", "Home", "History", "Favorites"],
            ["Connections"] = ["Connection Manager...", "Quick Connect...", "Reconnect", "Disconnect", "Test Connection"],
            ["Transfer"] = ["Enqueue", "Start Queue", "Pause All", "Resume All", "Cancel Selected", "Speed Limits..."],
            ["Sync"] = ["Compare Panes", "Preview Sync...", "Run Sync", "Sync Profiles...", "Schedules..."],
            ["Tools"] = ["Search...", "Checksums...", "Settings...", "Logs...", "Diagnostics..."],
            ["Help"] = ["Keyboard Shortcuts", "Documentation", "Report Issue", "About StorageHub"]
        };

    private static readonly IReadOnlyDictionary<string, (Keys Shortcut, UiGlyph? Glyph, string Description)> Metadata =
        new Dictionary<string, (Keys, UiGlyph?, string)>(StringComparer.Ordinal)
        {
            ["New Workspace Tab"] = (Keys.Control | Keys.T, UiGlyph.Add, "Open another independent dual-pane workspace."),
            ["Close Tab"] = (Keys.Control | Keys.W, null, "Close the active workspace tab."),
            ["Cut"] = (Keys.Control | Keys.X, null, "Queue selected files for a fenced move to the opposite pane."),
            ["Copy"] = (Keys.Control | Keys.C, null, "Queue selected files for a fenced copy to the opposite pane."),
            ["Paste"] = (Keys.Control | Keys.V, null, "Enqueue the staged operation in this pane."),
            ["Rename"] = (Keys.F2, null, "Rename the focused item."),
            ["Properties"] = (Keys.Alt | Keys.Enter, null, "Inspect read-only versions, metadata, and tags for one saved-connection file."),
            ["Select All"] = (Keys.Control | Keys.A, null, "Select every visible item."),
            ["Refresh"] = (Keys.F5, UiGlyph.Refresh, "Refresh the focused pane."),
            ["Back"] = (Keys.Alt | Keys.Left, UiGlyph.Back, "Return to the previous location."),
            ["Forward"] = (Keys.Alt | Keys.Right, UiGlyph.Forward, "Move to the next location in history."),
            ["Up"] = (Keys.Alt | Keys.Up, UiGlyph.Up, "Open the parent location."),
            ["Connection Manager..."] = (Keys.Control | Keys.Shift | Keys.M, UiGlyph.Connections, "Create, organize, and test connection profiles."),
            ["Quick Connect..."] = (Keys.Control | Keys.K, UiGlyph.Connections, "Open a temporary connection without saving credentials in the profile."),
            ["Enqueue"] = (Keys.F6, null, "Queue selected files for a fenced copy to the opposite pane."),
            ["Start Queue"] = (Keys.F7, UiGlyph.Run, "Start queued transfers."),
            ["Pause All"] = (Keys.F8, UiGlyph.Pause, "Pause active transfers at safe checkpoints."),
            ["Compare Panes"] = (Keys.Control | Keys.D, UiGlyph.Compare, "Compare the visible source and destination."),
            ["Preview Sync..."] = (Keys.Control | Keys.Shift | Keys.P, UiGlyph.Compare, "Generate a reviewable sync plan without applying changes."),
            ["Schedules..."] = (Keys.Control | Keys.Shift | Keys.S, UiGlyph.Run, "Manage durable preview-only synchronization schedules."),
            ["Search..."] = (Keys.Control | Keys.F, UiGlyph.Search, "Search within the focused endpoint."),
            ["Settings..."] = (Keys.Control | Keys.Oemcomma, null, "Configure StorageHub desktop and agent defaults.")
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
