using System.Text.Json;
using System.Text.Json.Serialization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// The desktop preferences that describe how StorageHub behaves anywhere, as opposed to the ones
/// that only mean something on the machine that wrote them.
/// </summary>
internal sealed record DesktopGeneralSection(
    bool CheckAutomatically,
    bool DownloadAutomatically,
    bool RestartAutomatically,
    bool IncludePrereleases,
    SshHostKeyDiscoveryMode SshHostKeyDiscovery,
    int MaximumEditableFileBytes,
    bool WarnBeforeUnsafeExternalEdit,
    bool AdaptiveConcurrency,
    int MinimumConcurrency,
    int MaximumTransferConcurrency,
    int PerConnectionConcurrency,
    int MaximumSyncConcurrency,
    DesktopAppearance Appearance,
    WorkspaceLayout DefaultWorkspaceLayout,
    SshTerminalPreferences? SshTerminal,
    bool ReconnectRemotePanesAutomatically,
    bool ConfirmBeforeClearingTransferHistory,
    bool ConfirmBeforeDeletingItems,
    int? DefaultWorkspacePaneCount);

/// <summary>
/// Settings that name something on one computer: a path, or a reference into that machine's
/// secret vault. Off by default when exporting, because they are noise or worse on any other
/// machine.
/// </summary>
internal sealed record MachineSpecificSection(
    string? ExternalEditorPath,
    IReadOnlyList<WorkspaceShortcutEntry>? PinnedWorkspaces,
    IReadOnlyList<WorkspaceShortcutEntry>? RecentWorkspaces,
    IReadOnlyDictionary<string, string>? PrivateKeyReferences);

/// <summary>
/// A saved connection as it travels: the profile draft exactly as the agent stores it, minus
/// nothing — credential fields are opaque vault references, never secrets. See
/// <see cref="SettingsExportFingerprint"/> for why the references are carried rather than blanked.
/// </summary>
internal sealed record ConnectionExportEntry(Guid ConnectionId, ConnectionProfileDraft Draft);

internal sealed record SyncProfileExportEntry(Guid ProfileId, SyncProfileDraftDocument Draft);

internal sealed record ScheduleExportEntry(Guid ScheduleId, ScheduleDraftDocument Draft);

/// <summary>
/// The whole export payload. A section is present exactly when its property is non-null; there is
/// deliberately no separate list of included sections, which would be a second source of truth
/// free to disagree with the content.
/// </summary>
internal sealed record SettingsExportDocument(
    int SchemaVersion,
    string FormatId,
    DateTimeOffset CreatedUtc,
    string Application,
    string? MachineFingerprint = null,
    DesktopGeneralSection? DesktopGeneral = null,
    IReadOnlyDictionary<string, Keys>? Shortcuts = null,
    IReadOnlyDictionary<string, string>? ConnectionDefaults = null,
    MachineSpecificSection? MachineSpecific = null,
    IReadOnlyList<ConnectionExportEntry>? Connections = null,
    IReadOnlyList<SyncProfileExportEntry>? SyncProfiles = null,
    IReadOnlyList<ScheduleExportEntry>? Schedules = null)
{
    /// <summary>Which sections this document actually carries.</summary>
    internal IReadOnlySet<SettingsSectionId> PresentSections()
    {
        var present = new HashSet<SettingsSectionId>();
        if (DesktopGeneral is not null) present.Add(SettingsSectionId.DesktopGeneral);
        if (Shortcuts is not null) present.Add(SettingsSectionId.Shortcuts);
        if (ConnectionDefaults is not null) present.Add(SettingsSectionId.ConnectionDefaults);
        if (MachineSpecific is not null) present.Add(SettingsSectionId.MachineSpecific);
        if (Connections is not null) present.Add(SettingsSectionId.Connections);
        if (SyncProfiles is not null) present.Add(SettingsSectionId.SyncProfiles);
        if (Schedules is not null) present.Add(SettingsSectionId.Schedules);
        return present;
    }

    internal int CountIn(SettingsSectionId section) => section switch
    {
        SettingsSectionId.Shortcuts => Shortcuts?.Count ?? 0,
        SettingsSectionId.ConnectionDefaults => ConnectionDefaults?.Count ?? 0,
        SettingsSectionId.Connections => Connections?.Count ?? 0,
        SettingsSectionId.SyncProfiles => SyncProfiles?.Count ?? 0,
        SettingsSectionId.Schedules => Schedules?.Count ?? 0,
        _ => 0
    };
}

/// <summary>Why a file could not be read as a settings export.</summary>
internal enum SettingsExportReadFailure
{
    None = 0,
    NotAnExport = 1,
    UnsupportedVersion = 2,
    TooLarge = 3,
    TooManyItems = 4
}

internal sealed record SettingsExportReadResult(
    SettingsExportDocument? Document,
    SettingsExportReadFailure Failure,
    string? Message);

internal static class SettingsExportSerializer
{
    internal const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Distinguishes a StorageHub export from any other JSON that happens to parse. Checked
    /// ordinally before anything in the document is trusted.
    /// </summary>
    internal const string FormatId = "storagehub.settings-export";

    internal const string FileExtension = ".shsettings";

    internal const string FileFilter = "StorageHub settings (*.shsettings)|*.shsettings|All files (*.*)|*.*";

    /// <summary>
    /// Caps applied before any per-item work. The agent's own list limits are the ceiling, so a
    /// file claiming more than the agent could ever return is refused rather than walked.
    /// </summary>
    internal const int MaximumConnections = StorageIpcLimits.MaximumConnectionResults;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static SettingsExportDocument Create(
        DateTimeOffset createdUtc,
        string application,
        string? machineFingerprint) => new(
            CurrentSchemaVersion,
            FormatId,
            createdUtc,
            application,
            machineFingerprint);

    internal static byte[] Serialize(SettingsExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    /// <summary>
    /// Parses a payload without throwing. Everything a caller needs to explain a refusal comes
    /// back in the result, because a malformed file is an ordinary thing for a user to pick.
    /// </summary>
    internal static SettingsExportReadResult Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > SettingsExportEnvelopeLimits.MaximumPayloadBytes)
        {
            return Failed(SettingsExportReadFailure.TooLarge, "This file is too large to be a StorageHub settings export.");
        }

        SettingsExportDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SettingsExportDocument>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return Failed(SettingsExportReadFailure.NotAnExport, NotAnExportMessage);
        }
        catch (NotSupportedException)
        {
            return Failed(SettingsExportReadFailure.NotAnExport, NotAnExportMessage);
        }

        if (document is null || !string.Equals(document.FormatId, FormatId, StringComparison.Ordinal))
        {
            return Failed(SettingsExportReadFailure.NotAnExport, NotAnExportMessage);
        }

        if (document.SchemaVersion < 1)
        {
            return Failed(SettingsExportReadFailure.NotAnExport, NotAnExportMessage);
        }

        if (document.SchemaVersion > CurrentSchemaVersion)
        {
            return Failed(
                SettingsExportReadFailure.UnsupportedVersion,
                $"This file was written by a newer version of StorageHub (format {document.SchemaVersion}). Update StorageHub and try again.");
        }

        // Cardinality before any per-item work, so an inflated file cannot cost a round trip per
        // claimed item.
        if (document.Connections?.Count > MaximumConnections ||
            document.SyncProfiles?.Count > SyncManagementIpcLimits.MaximumProfileResults ||
            document.Schedules?.Count > ScheduleManagementIpcLimits.MaximumScheduleResults)
        {
            return Failed(
                SettingsExportReadFailure.TooManyItems,
                "This file contains more items than StorageHub can import.");
        }

        return new SettingsExportReadResult(document, SettingsExportReadFailure.None, null);
    }

    private const string NotAnExportMessage = "This file is not a StorageHub settings export.";

    private static SettingsExportReadResult Failed(SettingsExportReadFailure failure, string message) =>
        new(null, failure, message);
}

/// <summary>
/// Mirrors the envelope's payload ceiling for the unencrypted path, so a plain file and a
/// protected one are bounded identically.
/// </summary>
internal static class SettingsExportEnvelopeLimits
{
    internal const int MaximumPayloadBytes = StorageHub.Security.SettingsExportEnvelope.MaximumPayloadBytes;

    internal const int MaximumFileBytes = StorageHub.Security.SettingsExportEnvelope.MaximumEnvelopeBytes;
}
