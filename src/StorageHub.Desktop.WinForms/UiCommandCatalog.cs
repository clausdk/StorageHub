namespace StorageHub.Desktop;

public sealed record UiCommandDefinition(
    string Id,
    string Menu,
    string Label,
    string Description,
    Keys Shortcut = Keys.None,
    UiGlyph? Glyph = null,
    UiIconTone Tone = UiIconTone.Text);

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
            ["Go"] = ["Back", "Forward", "Up", "Focus Address", "Next Pane", "Home", "History", "Favorites"],
            ["Connections"] = ["Connection Manager...", "Key Store...", "Quick Connect...", "Reconnect", "Disconnect", "Test Connection"],
            ["Transfer"] = ["Start Queue", "Pause All", "Resume All", "Cancel Selected", "Speed Limits..."],
            ["Sync"] = ["Compare Panes", "Review & Run...", "Sync Profiles...", "Schedules..."],
            ["Tools"] = ["Search...", "Background Agent...", "Checksums...", "Settings...", "Export Settings...", "Import Settings...", "Logs...", "Diagnostics..."],
            ["Help"] = ["Check for Updates...", "Keyboard Shortcuts", "Documentation", "Report Issue", "About StorageHub"]
        };

    /// <summary>
    /// Presentation for each command. Every entry carries a glyph: a menu where only some rows are
    /// illustrated reads as unfinished, and the icon column is what makes a long menu scannable.
    /// Destructive commands take the danger tone so the consequence is visible before the click.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (Keys Shortcut, UiGlyph? Glyph, UiIconTone Tone, string Description)> Metadata =
        new Dictionary<string, (Keys, UiGlyph?, UiIconTone, string)>(StringComparer.Ordinal)
        {
            ["New Workspace..."] = (Keys.Control | Keys.T, UiGlyph.Add, UiIconTone.Primary, "Choose a one- to four-pane workspace."),
            ["Open Workspace..."] = (Keys.Control | Keys.O, UiGlyph.Folder, UiIconTone.Text, "Open a saved StorageHub workspace file."),
            ["Save Workspace"] = (Keys.Control | Keys.S, UiGlyph.Save, UiIconTone.Text, "Save the active workspace."),
            ["Save Workspace As..."] = (Keys.Control | Keys.Shift | Keys.S, UiGlyph.Save, UiIconTone.Text, "Save the active workspace to a new file."),
            ["Rename Workspace..."] = (Keys.None, UiGlyph.Rename, UiIconTone.Text, "Rename the active workspace tab."),
            ["Close Workspace"] = (Keys.Control | Keys.W, UiGlyph.Close, UiIconTone.Text, "Close the active workspace tab."),
            ["Exit"] = (Keys.None, UiGlyph.Exit, UiIconTone.Text, "Close StorageHub."),
            ["Cut"] = (Keys.Control | Keys.X, UiGlyph.Cut, UiIconTone.Text, "Stage selected files for moving to another pane."),
            ["Copy"] = (Keys.Control | Keys.C, UiGlyph.Copy, UiIconTone.Text, "Stage selected files for copying to another pane."),
            ["Paste"] = (Keys.Control | Keys.V, UiGlyph.Paste, UiIconTone.Text, "Enqueue the staged operation in this pane."),
            ["New Folder"] = (Keys.Control | Keys.Shift | Keys.N, UiGlyph.Folder, UiIconTone.Text, "Create a folder in the active pane."),
            ["New Empty File..."] = (Keys.Control | Keys.Alt | Keys.N, UiGlyph.File, UiIconTone.Text, "Create an empty file in the active pane."),
            ["Rename"] = (Keys.F2, UiGlyph.Rename, UiIconTone.Text, "Rename the focused item."),
            ["Batch Rename..."] = (Keys.None, UiGlyph.Profiles, UiIconTone.Text, "Preview and rename several selected items."),
            ["Delete"] = (Keys.Delete, UiGlyph.Delete, UiIconTone.Danger, "Review and delete the selected items."),
            ["Invert Selection"] = (Keys.Control | Keys.I, UiGlyph.Invert, UiIconTone.Text, "Invert the visible selection in the active pane."),
            ["Properties"] = (Keys.Alt | Keys.Enter, UiGlyph.Properties, UiIconTone.Text, "Inspect read-only versions, metadata, and tags for one saved-connection file."),
            ["Select All"] = (Keys.Control | Keys.A, UiGlyph.SelectAll, UiIconTone.Text, "Select every visible item."),
            ["Refresh"] = (Keys.F5, UiGlyph.Refresh, UiIconTone.Text, "Refresh the focused pane."),
            ["Directory Tree"] = (Keys.None, UiGlyph.Tree, UiIconTone.Text, "Show or hide the directory tree beside the file list."),
            ["Transfer Queue"] = (Keys.None, UiGlyph.Queue, UiIconTone.Text, "Show or hide the transfer queue."),
            ["Session Log"] = (Keys.None, UiGlyph.Log, UiIconTone.Text, "Show or hide the session activity log."),
            ["Hidden Files"] = (Keys.None, UiGlyph.Hidden, UiIconTone.Text, "Show or hide items the provider marks as hidden."),
            ["Theme"] = (Keys.None, UiGlyph.Theme, UiIconTone.Text, "Switch between the light, dark, and system appearances."),
            ["Back"] = (Keys.Alt | Keys.Left, UiGlyph.Back, UiIconTone.Text, "Return to the previous location."),
            ["Forward"] = (Keys.Alt | Keys.Right, UiGlyph.Forward, UiIconTone.Text, "Move to the next location in history."),
            ["Up"] = (Keys.Alt | Keys.Up, UiGlyph.Up, UiIconTone.Text, "Open the parent location."),
            ["Focus Address"] = (Keys.Control | Keys.L, UiGlyph.Link, UiIconTone.Text, "Select the active pane's address."),
            ["Next Pane"] = (Keys.F6, UiGlyph.Layers, UiIconTone.Text, "Focus the next pane in the workspace."),
            ["Home"] = (Keys.None, UiGlyph.Home, UiIconTone.Text, "Open the pane's home location."),
            ["History"] = (Keys.None, UiGlyph.History, UiIconTone.Text, "Reopen a location visited in this session."),
            ["Favorites"] = (Keys.None, UiGlyph.Favorite, UiIconTone.Warning, "Open a connection you marked as a favorite."),
            ["Connection Manager..."] = (Keys.Control | Keys.Shift | Keys.M, UiGlyph.Connections, UiIconTone.Text, "Create, organize, and test connection profiles."),
            ["Key Store..."] = (Keys.Control | Keys.Shift | Keys.K, UiGlyph.Key, UiIconTone.Text, "Import and manage the certificates and private keys shared by saved connections."),
            ["Quick Connect..."] = (Keys.Control | Keys.K, UiGlyph.Connect, UiIconTone.Text, "Open a temporary connection without saving credentials in the profile."),
            ["Reconnect"] = (Keys.None, UiGlyph.Refresh, UiIconTone.Text, "Reopen the active pane's connection."),
            ["Disconnect"] = (Keys.None, UiGlyph.Disconnect, UiIconTone.Text, "Close the active pane's connection."),
            ["Test Connection"] = (Keys.None, UiGlyph.Test, UiIconTone.Success, "Check that the selected profile can reach its endpoint."),
            ["Start Queue"] = (Keys.F7, UiGlyph.Run, UiIconTone.Success, "Start queued transfers."),
            ["Pause All"] = (Keys.F8, UiGlyph.Pause, UiIconTone.Text, "Pause active transfers at safe checkpoints."),
            ["Resume All"] = (Keys.None, UiGlyph.Run, UiIconTone.Text, "Resume every paused transfer."),
            ["Cancel Selected"] = (Keys.None, UiGlyph.Stop, UiIconTone.Danger, "Cancel the selected transfers at their next safe checkpoint."),
            ["Speed Limits..."] = (Keys.None, UiGlyph.Speed, UiIconTone.Text, "Cap the bandwidth StorageHub transfers use."),
            ["Compare Panes"] = (Keys.Control | Keys.D, UiGlyph.Compare, UiIconTone.Text, "Compare the visible source and destination."),
            ["Review & Run..."] = (Keys.Control | Keys.Shift | Keys.P, UiGlyph.Test, UiIconTone.Text, "Review an exact sync plan, then run it when its safety checks pass."),
            ["Sync Profiles..."] = (Keys.None, UiGlyph.Profiles, UiIconTone.Text, "Create and edit saved synchronization tasks."),
            ["Schedules..."] = (Keys.Control | Keys.Alt | Keys.S, UiGlyph.Schedule, UiIconTone.Text, "Manage durable review-only or safety-gated automatic synchronization schedules."),
            ["Search..."] = (Keys.Control | Keys.F, UiGlyph.Search, UiIconTone.Text, "Search within the focused endpoint."),
            ["Background Agent..."] = (Keys.None, UiGlyph.Server, UiIconTone.Text, "Check whether the background agent is running and start, stop, or restart it."),
            ["Checksums..."] = (Keys.None, UiGlyph.Checksum, UiIconTone.Text, "Compute and compare content hashes for the selected items."),
            ["Settings..."] = (Keys.Control | Keys.Oemcomma, UiGlyph.Settings, UiIconTone.Text, "Configure automatic StorageHub updates."),
            ["Export Settings..."] = (Keys.None, UiGlyph.Upload, UiIconTone.Text, "Write your settings, connections, and sync tasks to a file, optionally password protected."),
            ["Import Settings..."] = (Keys.None, UiGlyph.Download, UiIconTone.Text, "Review a settings file and choose what to bring in."),
            ["Logs..."] = (Keys.None, UiGlyph.Log, UiIconTone.Text, "Open the StorageHub log directory."),
            ["Diagnostics..."] = (Keys.None, UiGlyph.Diagnostics, UiIconTone.Text, "Collect a support bundle describing this installation."),
            ["Check for Updates..."] = (Keys.None, UiGlyph.Refresh, UiIconTone.Text, "Check the official StorageHub GitHub releases for an update."),
            ["Keyboard Shortcuts"] = (Keys.None, UiGlyph.Keyboard, UiIconTone.Text, "Show and rebind the StorageHub keyboard shortcuts."),
            ["Documentation"] = (Keys.None, UiGlyph.Documentation, UiIconTone.Text, "Open the StorageHub documentation."),
            ["Report Issue"] = (Keys.None, UiGlyph.Bug, UiIconTone.Text, "Report a StorageHub defect."),
            ["About StorageHub"] = (Keys.None, UiGlyph.Info, UiIconTone.Text, "Show StorageHub version and application information.")
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
                var metadata = Metadata.GetValueOrDefault(
                    label,
                    (Keys.None, null, UiIconTone.Text, $"Run {label.TrimEnd('.').ToLowerInvariant()}."));
                definitions.Add(new UiCommandDefinition(
                    id,
                    menu,
                    label,
                    metadata.Description,
                    metadata.Shortcut,
                    metadata.Glyph,
                    metadata.Tone));
            }
        }

        return definitions.AsReadOnly();
    }
}
