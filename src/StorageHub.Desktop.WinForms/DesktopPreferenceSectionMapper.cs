using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>The outcome of overlaying an export file's desktop sections onto current settings.</summary>
/// <param name="Preferences">
/// The settings to save, or the unchanged current settings when nothing could be applied.
/// </param>
/// <param name="Applied">The sections that actually contributed.</param>
/// <param name="Blocked">
/// Sections present in the file but refused, keyed by section, with the reason to show the user.
/// A section is never partly applied: it lands whole or is reported.
/// </param>
internal sealed record DesktopPreferenceApplyResult(
    DesktopUpdatePreferences Preferences,
    IReadOnlySet<SettingsSectionId> Applied,
    IReadOnlyDictionary<SettingsSectionId, string> Blocked);

/// <summary>
/// Splits <see cref="DesktopUpdatePreferences"/> into the tickable sections an export carries, and
/// puts them back.
///
/// Capture and apply live together deliberately: they are two halves of one mapping, and a
/// property added to one but not the other is exactly the bug the completeness test in
/// <c>DesktopPreferenceSectionMapperTests</c> exists to catch.
/// </summary>
internal static class DesktopPreferenceSectionMapper
{
    internal static DesktopGeneralSection CaptureGeneral(DesktopUpdatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return new DesktopGeneralSection(
            preferences.CheckAutomatically,
            preferences.DownloadAutomatically,
            preferences.RestartAutomatically,
            preferences.IncludePrereleases,
            preferences.SshHostKeyDiscovery,
            preferences.MaximumEditableFileBytes,
            preferences.WarnBeforeUnsafeExternalEdit,
            preferences.AdaptiveConcurrency,
            preferences.MinimumConcurrency,
            preferences.MaximumTransferConcurrency,
            preferences.PerConnectionConcurrency,
            preferences.MaximumSyncConcurrency,
            preferences.Appearance,
            preferences.DefaultWorkspaceLayout,
            preferences.SshTerminal,
            preferences.ReconnectRemotePanesAutomatically,
            preferences.ConfirmBeforeClearingTransferHistory,
            preferences.ConfirmBeforeDeletingItems,
            preferences.DefaultWorkspacePaneCount);
    }

    /// <summary>
    /// The connection defaults worth carrying, with every private-key reference removed. Those
    /// name a secret in this machine's vault and belong to the machine-specific section.
    /// </summary>
    internal static Dictionary<string, string> CaptureConnectionDefaults(DesktopUpdatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return ConnectionDefaultSettings.Normalize(preferences.ConnectionDefaults)
            .Where(pair => !IsPrivateKeyReference(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    internal static MachineSpecificSection CaptureMachineSpecific(DesktopUpdatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return new MachineSpecificSection(
            preferences.ExternalEditorPath,
            preferences.PinnedWorkspaces,
            preferences.RecentWorkspaces,
            ConnectionDefaultSettings.Normalize(preferences.ConnectionDefaults)
                .Where(pair => IsPrivateKeyReference(pair.Key) && pair.Value.Length > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    /// <summary>
    /// Overlays the selected sections of an export onto the settings already in use.
    ///
    /// Starting from the current settings rather than from defaults is what makes "choose what to
    /// import" honest: an unticked section must leave its properties exactly as they were, not
    /// reset them.
    /// </summary>
    internal static DesktopPreferenceApplyResult Apply(
        DesktopUpdatePreferences current,
        SettingsExportDocument document,
        IReadOnlyCollection<SettingsSectionId> chosen)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chosen);

        // Taken as a collection rather than a set so every caller can pass a collection
        // expression; the membership tests below want a set.
        var selected = chosen as IReadOnlySet<SettingsSectionId> ?? chosen.ToHashSet();
        var applied = new HashSet<SettingsSectionId>();
        var blocked = new Dictionary<SettingsSectionId, string>();
        var updated = current;

        if (selected.Contains(SettingsSectionId.DesktopGeneral) && document.DesktopGeneral is { } general)
        {
            var candidate = ApplyGeneral(updated, general);
            // The same bounds the Settings dialog is held to, so an imported value can never be
            // one the app would have refused to save.
            if (DesktopUpdatePreferencesStore.Validate(candidate) is { } error)
            {
                blocked[SettingsSectionId.DesktopGeneral] = error;
            }
            else
            {
                updated = candidate;
                applied.Add(SettingsSectionId.DesktopGeneral);
            }
        }

        if (selected.Contains(SettingsSectionId.Shortcuts) && document.Shortcuts is { } shortcuts)
        {
            // Validated before resolving. ShortcutSettings.Resolve silently falls back to the full
            // default set when given an invalid map, which during an import would wipe every
            // shortcut the user had without a word.
            if (ShortcutSettings.Validate(shortcuts) is { } error)
            {
                blocked[SettingsSectionId.Shortcuts] = error;
            }
            else
            {
                updated = updated with { Shortcuts = ShortcutSettings.Resolve(shortcuts) };
                applied.Add(SettingsSectionId.Shortcuts);
            }
        }

        var importsDefaults = selected.Contains(SettingsSectionId.ConnectionDefaults) &&
            document.ConnectionDefaults is not null;
        var importsMachine = selected.Contains(SettingsSectionId.MachineSpecific) &&
            document.MachineSpecific is not null;

        if (importsDefaults || importsMachine)
        {
            updated = updated with
            {
                ConnectionDefaults = MergeConnectionDefaults(
                    updated,
                    importsDefaults ? document.ConnectionDefaults : null,
                    importsMachine ? document.MachineSpecific?.PrivateKeyReferences : null)
            };
            if (importsDefaults) applied.Add(SettingsSectionId.ConnectionDefaults);
        }

        if (importsMachine && document.MachineSpecific is { } machine)
        {
            updated = ApplyMachineSpecific(updated, machine, blocked);
            applied.Add(SettingsSectionId.MachineSpecific);
        }

        return new DesktopPreferenceApplyResult(updated, applied, blocked);
    }

    private static DesktopUpdatePreferences ApplyGeneral(
        DesktopUpdatePreferences current,
        DesktopGeneralSection general) => current with
        {
            CheckAutomatically = general.CheckAutomatically,
            DownloadAutomatically = general.DownloadAutomatically,
            RestartAutomatically = general.RestartAutomatically,
            IncludePrereleases = general.IncludePrereleases,
            SshHostKeyDiscovery = Enum.IsDefined(general.SshHostKeyDiscovery)
                ? general.SshHostKeyDiscovery
                : current.SshHostKeyDiscovery,
            MaximumEditableFileBytes = general.MaximumEditableFileBytes,
            WarnBeforeUnsafeExternalEdit = general.WarnBeforeUnsafeExternalEdit,
            AdaptiveConcurrency = general.AdaptiveConcurrency,
            MinimumConcurrency = general.MinimumConcurrency,
            MaximumTransferConcurrency = general.MaximumTransferConcurrency,
            PerConnectionConcurrency = general.PerConnectionConcurrency,
            MaximumSyncConcurrency = general.MaximumSyncConcurrency,
            Appearance = Enum.IsDefined(general.Appearance) ? general.Appearance : current.Appearance,
            DefaultWorkspaceLayout = Enum.IsDefined(general.DefaultWorkspaceLayout)
                ? general.DefaultWorkspaceLayout
                : current.DefaultWorkspaceLayout,
            // Resolve clamps every field, so a hand-edited terminal size cannot break the shell.
            SshTerminal = general.SshTerminal is null
                ? current.SshTerminal
                : SshTerminalPreferences.Resolve(general.SshTerminal),
            ReconnectRemotePanesAutomatically = general.ReconnectRemotePanesAutomatically,
            ConfirmBeforeClearingTransferHistory = general.ConfirmBeforeClearingTransferHistory,
            ConfirmBeforeDeletingItems = general.ConfirmBeforeDeletingItems,
            DefaultWorkspacePaneCount = general.DefaultWorkspacePaneCount
        };

    private static DesktopUpdatePreferences ApplyMachineSpecific(
        DesktopUpdatePreferences current,
        MachineSpecificSection machine,
        Dictionary<SettingsSectionId, string> blocked)
    {
        var editorPath = current.ExternalEditorPath;
        if (machine.ExternalEditorPath is { } imported)
        {
            if (DesktopUpdatePreferencesStore.IsValidEditorPath(imported))
            {
                editorPath = imported;
            }
            else
            {
                blocked[SettingsSectionId.MachineSpecific] =
                    "The external editor path in this file is not usable and was left unchanged.";
            }
        }

        // Resolve rather than Validate: workspace lists are bookmarks, and dropping entries that
        // no longer make sense is friendlier than refusing the whole list. Nothing breaks when a
        // pinned workspace is missing — it renders dimmed and offers to remove itself.
        return current with
        {
            ExternalEditorPath = editorPath,
            PinnedWorkspaces = machine.PinnedWorkspaces is null
                ? current.PinnedWorkspaces
                : WorkspaceShortcutSettings.Resolve(
                    machine.PinnedWorkspaces, WorkspaceShortcutSettings.MaximumPinned),
            RecentWorkspaces = machine.RecentWorkspaces is null
                ? current.RecentWorkspaces
                : WorkspaceShortcutSettings.Resolve(
                    machine.RecentWorkspaces, WorkspaceShortcutSettings.MaximumRecent)
        };
    }

    /// <summary>
    /// Overlays imported defaults onto the current ones before normalizing.
    ///
    /// This ordering is load-bearing. <see cref="ConnectionDefaultSettings.Normalize"/> rebuilds
    /// the complete key set for every provider, so handing it only the imported subset would reset
    /// every key the file did not mention — including this machine's private-key reference
    /// whenever the machine-specific section was left unticked.
    /// </summary>
    private static Dictionary<string, string> MergeConnectionDefaults(
        DesktopUpdatePreferences current,
        IReadOnlyDictionary<string, string>? importedDefaults,
        IReadOnlyDictionary<string, string>? importedPrivateKeys)
    {
        var merged = ConnectionDefaultSettings.Normalize(current.ConnectionDefaults);
        if (importedDefaults is not null)
        {
            foreach (var pair in importedDefaults)
            {
                // A private-key reference is never taken from this section even if a hand-edited
                // file puts one there; it belongs to the machine-specific section alone.
                if (IsPrivateKeyReference(pair.Key)) continue;
                merged[pair.Key] = pair.Value;
            }
        }

        if (importedPrivateKeys is not null)
        {
            foreach (var pair in importedPrivateKeys)
            {
                if (!IsPrivateKeyReference(pair.Key)) continue;
                // A reference that is not shaped like one would fail the settings bounds check
                // later; drop it here so one bad entry cannot block the whole section.
                if (!ConnectionEndpointDocument.IsOpaqueSecretReference(pair.Value)) continue;
                merged[pair.Key] = pair.Value;
            }
        }

        return ConnectionDefaultSettings.Normalize(merged);
    }

    private static bool IsPrivateKeyReference(string key) =>
        key.EndsWith($".{ConnectionDefaultSettings.PrivateKeyReferenceKey}", StringComparison.Ordinal);
}
