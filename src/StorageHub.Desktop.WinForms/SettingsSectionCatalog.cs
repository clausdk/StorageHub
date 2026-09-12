namespace StorageHub.Desktop;

/// <summary>
/// The parts of a StorageHub configuration that can be exported and imported independently.
/// Stable numbers: they are not persisted, but the export file's section keys are derived from
/// this enum and reordering it would be a silent format change.
/// </summary>
internal enum SettingsSectionId
{
    DesktopGeneral = 1,
    Shortcuts = 2,
    ConnectionDefaults = 3,
    MachineSpecific = 4,
    Connections = 5,
    SyncProfiles = 6,
    Schedules = 7
}

/// <summary>
/// One tickable section, described once and used by both the export dialog and the import wizard
/// so the two can never offer different things.
/// </summary>
/// <param name="Key">The JSON property name carrying this section in an export file.</param>
/// <param name="CheckedByDefault">
/// Whether the section starts ticked. This is the only place the "off by default" decision for
/// machine-specific settings is expressed.
/// </param>
/// <param name="RequiresAgent">
/// Whether reading or writing the section needs the background agent. These sections are disabled
/// with an explanation when the agent is unreachable, rather than failing midway.
/// </param>
/// <param name="DependsOn">
/// Sections this one is meaningless without. Enforced strictly when exporting and leniently when
/// importing; see <see cref="ExpandForExport"/>.
/// </param>
internal sealed record SettingsSectionDefinition(
    SettingsSectionId Id,
    string Key,
    string Label,
    string Description,
    bool CheckedByDefault,
    bool RequiresAgent,
    IReadOnlyList<SettingsSectionId> DependsOn);

internal static class SettingsSectionCatalog
{
    internal static IReadOnlyList<SettingsSectionDefinition> Sections { get; } =
    [
        new(
            SettingsSectionId.DesktopGeneral,
            "desktopGeneral",
            "Desktop preferences",
            "Appearance, transfer and sync concurrency, editing limits, update checks, and workspace defaults.",
            CheckedByDefault: true,
            RequiresAgent: false,
            []),
        new(
            SettingsSectionId.Shortcuts,
            "shortcuts",
            "Keyboard shortcuts",
            "Every rebindable command shortcut.",
            CheckedByDefault: true,
            RequiresAgent: false,
            []),
        new(
            SettingsSectionId.ConnectionDefaults,
            "connectionDefaults",
            "Connection defaults",
            "The per-provider timeouts, ports, and options new connections start from.",
            CheckedByDefault: true,
            RequiresAgent: false,
            []),
        new(
            SettingsSectionId.MachineSpecific,
            "machineSpecific",
            "This computer only",
            "Editor path, pinned and recent workspaces, and the private key chosen in connection " +
            "defaults. These point at this computer, so leave this off when sharing the file.",
            CheckedByDefault: false,
            RequiresAgent: false,
            []),
        new(
            SettingsSectionId.Connections,
            "connections",
            "Connection profiles",
            "Saved connections without their credentials, which never leave this computer.",
            CheckedByDefault: true,
            RequiresAgent: true,
            []),
        new(
            SettingsSectionId.SyncProfiles,
            "syncProfiles",
            "Sync tasks",
            "Sync task definitions, including their filters and safety limits.",
            CheckedByDefault: true,
            RequiresAgent: true,
            [SettingsSectionId.Connections]),
        new(
            SettingsSectionId.Schedules,
            "schedules",
            "Schedules",
            "When each sync task runs.",
            CheckedByDefault: true,
            RequiresAgent: true,
            [SettingsSectionId.SyncProfiles])
    ];

    internal static SettingsSectionDefinition Get(SettingsSectionId id) =>
        Sections.FirstOrDefault(section => section.Id == id) ??
        throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown settings section.");

    /// <summary>The sections ticked when a dialog first opens.</summary>
    internal static IReadOnlySet<SettingsSectionId> Defaults =>
        Sections.Where(section => section.CheckedByDefault).Select(section => section.Id).ToHashSet();

    /// <summary>
    /// Adds the sections the chosen ones cannot stand without. Export is strict: a schedule whose
    /// sync task is absent from the file, or a sync task whose connections are absent, would
    /// describe something the file cannot reconstruct.
    ///
    /// Import deliberately does not do this — there, a dependency may already exist on the target
    /// machine, and demanding it be in the file too would block every partial restore.
    /// </summary>
    internal static IReadOnlySet<SettingsSectionId> ExpandForExport(IEnumerable<SettingsSectionId> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var expanded = new HashSet<SettingsSectionId>(selected);
        // One pass per section is enough for any acyclic graph of this size, and the catalog test
        // proves the graph is acyclic.
        for (var pass = 0; pass < Sections.Count; pass++)
        {
            var added = false;
            foreach (var id in expanded.ToArray())
            {
                foreach (var dependency in Get(id).DependsOn)
                {
                    added |= expanded.Add(dependency);
                }
            }

            if (!added) break;
        }

        return expanded;
    }

    /// <summary>
    /// Removes sections whose dependencies have just been unticked, so the export dialog cannot
    /// leave "Schedules" ticked after "Connection profiles" is cleared.
    /// </summary>
    internal static IReadOnlySet<SettingsSectionId> CollapseForExport(IEnumerable<SettingsSectionId> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var remaining = new HashSet<SettingsSectionId>(selected);
        for (var pass = 0; pass < Sections.Count; pass++)
        {
            var removed = remaining.RemoveWhere(id => Get(id).DependsOn.Any(dependency => !remaining.Contains(dependency)));
            if (removed == 0) break;
        }

        return remaining;
    }

    /// <summary>
    /// The order sections must be applied in: a dependency is always applied before whatever needs
    /// it, so an imported sync task can resolve the connection it points at.
    /// </summary>
    internal static IReadOnlyList<SettingsSectionDefinition> ApplyOrder { get; } =
        [.. Sections.OrderBy(section => DependencyDepth(section.Id)).ThenBy(section => (int)section.Id)];

    private static int DependencyDepth(SettingsSectionId id)
    {
        var depth = 0;
        foreach (var dependency in Get(id).DependsOn)
        {
            depth = Math.Max(depth, DependencyDepth(dependency) + 1);
        }

        return depth;
    }
}
