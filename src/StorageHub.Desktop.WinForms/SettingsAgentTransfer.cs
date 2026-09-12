using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>The agent-side clients the settings transfer needs, bundled so callers pass one thing.</summary>
internal sealed record SettingsAgentClients(
    IRemoteStorageAgentClient Storage,
    IRemoteConnectionProfileClient Profiles,
    ISyncManagementAgentClient Sync,
    IScheduleManagementAgentClient Schedules);

/// <summary>What to do about an imported item whose name or id is already taken.</summary>
internal enum SettingsConflictPolicy
{
    /// <summary>Leave what is here alone. The imported item is still mapped, so dependants resolve.</summary>
    Skip = 0,

    /// <summary>Overwrite the local item with the imported one.</summary>
    Replace = 1,

    /// <summary>Keep both, giving the imported one a free name.</summary>
    ImportAsCopy = 2
}

/// <summary>The outcome for one agent-backed item.</summary>
internal sealed record SettingsItemOutcome(string Name, string Result, bool NeedsCredentials = false);

/// <summary>What happened to the agent-backed sections of an import.</summary>
internal sealed record SettingsAgentImportResult(
    IReadOnlyList<SettingsItemOutcome> Connections,
    IReadOnlyList<SettingsItemOutcome> SyncProfiles,
    IReadOnlyList<SettingsItemOutcome> Schedules)
{
    internal static SettingsAgentImportResult Empty { get; } = new([], [], []);

    internal IEnumerable<SettingsItemOutcome> All => Connections.Concat(SyncProfiles).Concat(Schedules);

    internal int Count => Connections.Count + SyncProfiles.Count + Schedules.Count;
}

/// <summary>
/// Reads connections, sync tasks and schedules out of the agent and puts them back.
///
/// Everything here is non-secret by construction: the agent stores opaque references to
/// credentials, never the credentials themselves, and the pipe that holds the real material is
/// write-only. An exported connection therefore carries a pointer that resolves on the machine
/// that wrote it and nowhere else — see <see cref="SettingsExportFingerprint"/> for how import
/// decides between the two cases.
/// </summary>
internal static class SettingsAgentTransfer
{
    /// <summary>Marks a connection whose credentials could not travel with it.</summary>
    internal const string NeedsCredentialsTag = "needs-credentials";

    internal static async Task<SettingsExportDocument> CaptureAsync(
        SettingsExportDocument document,
        IReadOnlyCollection<SettingsSectionId> selected,
        SettingsAgentClients clients,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(clients);

        if (selected.Contains(SettingsSectionId.Connections))
        {
            document = document with { Connections = await CaptureConnectionsAsync(clients, cancellationToken).ConfigureAwait(false) };
        }

        if (selected.Contains(SettingsSectionId.SyncProfiles))
        {
            document = document with { SyncProfiles = await CaptureSyncProfilesAsync(clients, cancellationToken).ConfigureAwait(false) };
        }

        if (selected.Contains(SettingsSectionId.Schedules))
        {
            document = document with { Schedules = await CaptureSchedulesAsync(clients, cancellationToken).ConfigureAwait(false) };
        }

        return document;
    }

    private static async Task<List<ConnectionExportEntry>> CaptureConnectionsAsync(
        SettingsAgentClients clients,
        CancellationToken cancellationToken)
    {
        // The listing carries summaries only, so the full draft needs one round trip each. Disabled
        // profiles are included: a configuration is not complete without the ones you turned off.
        var listed = await clients.Storage.ListConnectionsAsync(
            new ConnectionListRequest(IncludeDisabled: true, Limit: StorageIpcLimits.MaximumConnectionResults),
            cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(listed.Failure);

        var captured = new List<ConnectionExportEntry>(listed.Connections.Length);
        foreach (var summary in listed.Connections)
        {
            var profile = await clients.Profiles.GetAsync(
                new ConnectionProfileGetRequest(ConnectionProfileIpcContract.CurrentVersion, summary.ConnectionId),
                cancellationToken).ConfigureAwait(false);
            if (profile.Profile is { } document)
            {
                captured.Add(new ConnectionExportEntry(document.ConnectionId, document.Draft));
            }
        }

        return captured;
    }

    private static async Task<List<SyncProfileExportEntry>> CaptureSyncProfilesAsync(
        SettingsAgentClients clients,
        CancellationToken cancellationToken)
    {
        var listed = await clients.Sync.ListProfilesAsync(
            new SyncProfileListRequest(), cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(listed.Failure);

        var captured = new List<SyncProfileExportEntry>(listed.Profiles.Length);
        foreach (var summary in listed.Profiles)
        {
            var profile = await clients.Sync.GetProfileAsync(
                new SyncProfileGetRequest(SyncManagementIpcContract.CurrentVersion, summary.ProfileId), cancellationToken).ConfigureAwait(false);
            if (profile.Profile is { } document)
            {
                captured.Add(new SyncProfileExportEntry(document.ProfileId, document.Draft));
            }
        }

        return captured;
    }

    private static async Task<List<ScheduleExportEntry>> CaptureSchedulesAsync(
        SettingsAgentClients clients,
        CancellationToken cancellationToken)
    {
        var listed = await clients.Schedules.ListAsync(
            new ScheduleListRequest(), cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(listed.Failure);

        return [.. listed.Schedules.Select(schedule => new ScheduleExportEntry(
            schedule.ScheduleId,
            new ScheduleDraftDocument(
                schedule.ProfileId,
                schedule.CronExpression,
                schedule.TimeZoneId,
                schedule.MisfireGraceSeconds,
                schedule.QueueOneWhileRunning,
                schedule.Enabled,
                schedule.ExecutionMode)))];
    }

    /// <summary>
    /// Writes the agent-backed sections back, in dependency order, remapping identifiers as it
    /// goes. Each item is reported individually: one failure must not abandon the rest, because
    /// there is no transaction spanning separate agent calls to roll back to.
    /// </summary>
    internal static async Task<SettingsAgentImportResult> ApplyAsync(
        SettingsExportDocument document,
        IReadOnlyCollection<SettingsSectionId> selected,
        SettingsAgentClients clients,
        SettingsConflictPolicy policy,
        bool sameMachine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(clients);

        var map = new SettingsImportIdMap();
        var connections = selected.Contains(SettingsSectionId.Connections) && document.Connections is { } imported
            ? await ApplyConnectionsAsync(imported, clients, policy, sameMachine, map, cancellationToken).ConfigureAwait(false)
            : [];
        var syncProfiles = selected.Contains(SettingsSectionId.SyncProfiles) && document.SyncProfiles is { } syncs
            ? await ApplySyncProfilesAsync(syncs, clients, policy, map, cancellationToken).ConfigureAwait(false)
            : [];
        var schedules = selected.Contains(SettingsSectionId.Schedules) && document.Schedules is { } plans
            ? await ApplySchedulesAsync(plans, clients, policy, map, cancellationToken).ConfigureAwait(false)
            : [];

        return new SettingsAgentImportResult(connections, syncProfiles, schedules);
    }

    private static async Task<List<SettingsItemOutcome>> ApplyConnectionsAsync(
        IReadOnlyList<ConnectionExportEntry> imported,
        SettingsAgentClients clients,
        SettingsConflictPolicy policy,
        bool sameMachine,
        SettingsImportIdMap map,
        CancellationToken cancellationToken)
    {
        var listed = await clients.Storage.ListConnectionsAsync(
            new ConnectionListRequest(IncludeDisabled: true, Limit: StorageIpcLimits.MaximumConnectionResults),
            cancellationToken).ConfigureAwait(false);
        var existing = listed.Connections;
        var takenNames = existing
            .Select(connection => connection.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outcomes = new List<SettingsItemOutcome>(imported.Count);
        foreach (var entry in imported)
        {
            var draft = PrepareConnection(entry.Draft, sameMachine, out var needsCredentials);
            var name = draft.Metadata.DisplayName;
            var match = existing.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayName, name, StringComparison.OrdinalIgnoreCase));

            if (match is not null && policy == SettingsConflictPolicy.Skip)
            {
                // Mapped even though nothing was written, so a sync task pointing at this
                // connection finds the copy that is already here.
                map.RecordConnection(entry.ConnectionId, match.ConnectionId);
                outcomes.Add(new SettingsItemOutcome(name, "Already here, left alone"));
                continue;
            }

            if (match is not null && policy == SettingsConflictPolicy.Replace)
            {
                var replaced = await clients.Profiles.UpdateAsync(
                    new ConnectionProfileUpdateRequest(
                        ConnectionProfileIpcContract.CurrentVersion, match.ConnectionId, match.Version, draft),
                    cancellationToken).ConfigureAwait(false);
                if (replaced.Status == ConnectionProfileWriteStatus.Succeeded)
                {
                    map.RecordConnection(entry.ConnectionId, match.ConnectionId);
                    outcomes.Add(new SettingsItemOutcome(name, "Replaced", needsCredentials));
                }
                else
                {
                    outcomes.Add(new SettingsItemOutcome(name, Describe(replaced.Status)));
                }

                continue;
            }

            if (match is not null)
            {
                draft = draft with
                {
                    Metadata = draft.Metadata with { DisplayName = FreeName(name, takenNames) }
                };
            }

            var created = await clients.Profiles.CreateAsync(
                new ConnectionProfileCreateRequest(ConnectionProfileIpcContract.CurrentVersion, draft),
                cancellationToken).ConfigureAwait(false);
            if (created is { Status: ConnectionProfileWriteStatus.Succeeded, Profile: { } profile })
            {
                map.RecordConnection(entry.ConnectionId, profile.ConnectionId);
                _ = takenNames.Add(draft.Metadata.DisplayName);
                outcomes.Add(new SettingsItemOutcome(draft.Metadata.DisplayName, "Added", needsCredentials));
            }
            else
            {
                outcomes.Add(new SettingsItemOutcome(name, Describe(created.Status)));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Adjusts an imported connection for where it is landing.
    ///
    /// Credential references are carried verbatim, which is what makes restoring a backup on the
    /// same machine work completely. Arriving anywhere else they resolve to nothing, so the
    /// profile is brought in disabled and tagged rather than left looking ready to use — it cannot
    /// be brought in without them, because a profile with its references blanked fails validation.
    /// </summary>
    private static ConnectionProfileDraft PrepareConnection(
        ConnectionProfileDraft draft,
        bool sameMachine,
        out bool needsCredentials)
    {
        needsCredentials = !sameMachine && draft.Authentication.Kind is not
            (ConnectionAuthenticationKind.None or ConnectionAuthenticationKind.S3DefaultCredentialChain);
        if (!needsCredentials) return draft;

        // Tags are declared nullable on the wire, and a hand-edited file can omit them.
        var tags = draft.Metadata.Tags ?? [];
        if (!tags.Contains(NeedsCredentialsTag, StringComparer.Ordinal) &&
            tags.Length < ConnectionProfileIpcLimits.MaximumTagCount)
        {
            tags = [.. tags, NeedsCredentialsTag];
        }

        return draft with
        {
            IsEnabled = false,
            Metadata = draft.Metadata with { Tags = tags }
        };
    }

    private static async Task<List<SettingsItemOutcome>> ApplySyncProfilesAsync(
        IReadOnlyList<SyncProfileExportEntry> imported,
        SettingsAgentClients clients,
        SettingsConflictPolicy policy,
        SettingsImportIdMap map,
        CancellationToken cancellationToken)
    {
        var connections = await clients.Storage.ListConnectionsAsync(
            new ConnectionListRequest(IncludeDisabled: true, Limit: StorageIpcLimits.MaximumConnectionResults),
            cancellationToken).ConfigureAwait(false);
        var connectionIds = connections.Connections.Select(connection => connection.ConnectionId).ToHashSet();
        var listed = await clients.Sync.ListProfilesAsync(new SyncProfileListRequest(), cancellationToken)
            .ConfigureAwait(false);
        var existing = listed.Profiles;

        var outcomes = new List<SettingsItemOutcome>(imported.Count);
        foreach (var entry in imported)
        {
            // A sync task is meaningless without both ends, so one that cannot be pointed at a
            // real connection is reported rather than created broken.
            if (!map.TryResolveConnection(entry.Draft.LeftConnectionId, connectionIds, out var left) ||
                !map.TryResolveConnection(entry.Draft.RightConnectionId, connectionIds, out var right))
            {
                outcomes.Add(new SettingsItemOutcome(
                    entry.Draft.DisplayName, "Skipped, its connections are not here"));
                continue;
            }

            var draft = entry.Draft with { LeftConnectionId = left, RightConnectionId = right };
            var match = existing.FirstOrDefault(candidate =>
                string.Equals(candidate.DisplayName, draft.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (match is not null && policy == SettingsConflictPolicy.Skip)
            {
                map.RecordSyncProfile(entry.ProfileId, match.ProfileId);
                outcomes.Add(new SettingsItemOutcome(draft.DisplayName, "Already here, left alone"));
                continue;
            }

            if (match is not null && policy == SettingsConflictPolicy.Replace)
            {
                var replaced = await clients.Sync.UpdateProfileAsync(
                    new SyncProfileUpdateRequest(SyncManagementIpcContract.CurrentVersion, match.ProfileId, match.Revision, draft),
                    cancellationToken).ConfigureAwait(false);
                map.RecordSyncProfile(entry.ProfileId, match.ProfileId);
                outcomes.Add(new SettingsItemOutcome(draft.DisplayName, Describe(replaced)));
                continue;
            }

            // Sync profiles accept a caller-supplied id, so a restore keeps the original unless
            // that id is already taken.
            var profileId = existing.Any(candidate => candidate.ProfileId == entry.ProfileId)
                ? Guid.NewGuid()
                : entry.ProfileId;
            if (match is not null)
            {
                draft = draft with
                {
                    DisplayName = FreeName(
                        draft.DisplayName,
                        existing.Select(candidate => candidate.DisplayName)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase))
                };
            }

            var created = await clients.Sync.CreateProfileAsync(
                new SyncProfileCreateRequest(SyncManagementIpcContract.CurrentVersion, profileId, draft), cancellationToken).ConfigureAwait(false);
            if (created.Outcome is SyncProfileMutationOutcome.Succeeded or SyncProfileMutationOutcome.AlreadyApplied)
            {
                map.RecordSyncProfile(entry.ProfileId, profileId);
                outcomes.Add(new SettingsItemOutcome(draft.DisplayName, "Added"));
            }
            else
            {
                outcomes.Add(new SettingsItemOutcome(draft.DisplayName, Describe(created)));
            }
        }

        return outcomes;
    }

    private static async Task<List<SettingsItemOutcome>> ApplySchedulesAsync(
        IReadOnlyList<ScheduleExportEntry> imported,
        SettingsAgentClients clients,
        SettingsConflictPolicy policy,
        SettingsImportIdMap map,
        CancellationToken cancellationToken)
    {
        var syncProfiles = await clients.Sync.ListProfilesAsync(new SyncProfileListRequest(), cancellationToken)
            .ConfigureAwait(false);
        var profileIds = syncProfiles.Profiles.Select(profile => profile.ProfileId).ToHashSet();
        var listed = await clients.Schedules.ListAsync(new ScheduleListRequest(), cancellationToken)
            .ConfigureAwait(false);
        var existing = listed.Schedules;

        var outcomes = new List<SettingsItemOutcome>(imported.Count);
        foreach (var entry in imported)
        {
            var name = $"{entry.Draft.CronExpression} ({entry.Draft.TimeZoneId})";
            if (!map.TryResolveSyncProfile(entry.Draft.ProfileId, profileIds, out var profileId))
            {
                outcomes.Add(new SettingsItemOutcome(name, "Skipped, its sync task is not here"));
                continue;
            }

            // Time zone ids are per-machine data. A schedule naming one this computer does not
            // have would be accepted and then never fire.
            if (!TimeZoneExists(entry.Draft.TimeZoneId))
            {
                outcomes.Add(new SettingsItemOutcome(
                    name, $"Skipped, this computer has no time zone called {entry.Draft.TimeZoneId}"));
                continue;
            }

            var draft = entry.Draft with { ProfileId = profileId };
            var match = existing.FirstOrDefault(candidate => candidate.ProfileId == profileId);
            if (match is not null && policy == SettingsConflictPolicy.Skip)
            {
                outcomes.Add(new SettingsItemOutcome(name, "Already here, left alone"));
                continue;
            }

            if (match is not null && policy == SettingsConflictPolicy.Replace)
            {
                var replaced = await clients.Schedules.UpdateAsync(
                    new ScheduleUpdateRequest(ScheduleManagementIpcContract.CurrentVersion, match.ScheduleId, match.Revision, draft), cancellationToken)
                    .ConfigureAwait(false);
                outcomes.Add(new SettingsItemOutcome(name, Describe(replaced)));
                continue;
            }

            var scheduleId = existing.Any(candidate => candidate.ScheduleId == entry.ScheduleId)
                ? Guid.NewGuid()
                : entry.ScheduleId;
            var created = await clients.Schedules.CreateAsync(
                new ScheduleCreateRequest(ScheduleManagementIpcContract.CurrentVersion, scheduleId, draft),
                cancellationToken).ConfigureAwait(false);
            outcomes.Add(new SettingsItemOutcome(
                name,
                created.Outcome is ScheduleMutationOutcome.Succeeded or ScheduleMutationOutcome.AlreadyApplied
                    ? "Added"
                    : Describe(created)));
        }

        return outcomes;
    }

    private static bool TimeZoneExists(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or
            ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Derives a name nothing else is using. The base is what gets trimmed, never the suffix, so
    /// the result stays inside the display-name limit and still reads as a copy.
    /// </summary>
    internal static string FreeName(string name, IReadOnlySet<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        for (var attempt = 1; attempt < 100; attempt++)
        {
            var suffix = attempt == 1 ? " (imported)" : $" (imported {attempt})";
            var room = ConnectionProfileIpcLimits.MaximumDisplayNameLength - suffix.Length;
            var candidate = (name.Length > room ? name[..room] : name) + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }

        return $"{Guid.NewGuid():N}";
    }

    private static string Describe(SyncProfileMutationResponse response) => response.Outcome switch
    {
        SyncProfileMutationOutcome.Succeeded or SyncProfileMutationOutcome.AlreadyApplied => "Replaced",
        SyncProfileMutationOutcome.RevisionConflict => "Skipped, it changed while importing",
        SyncProfileMutationOutcome.ConstraintConflict => "Skipped, it conflicts with one already here",
        SyncProfileMutationOutcome.Unavailable => "Skipped, the agent was unavailable",
        _ => response.Failure?.Message ?? "Skipped"
    };

    private static string Describe(ScheduleMutationResponse response) => response.Outcome switch
    {
        ScheduleMutationOutcome.Succeeded or ScheduleMutationOutcome.AlreadyApplied => "Replaced",
        ScheduleMutationOutcome.RevisionConflict => "Skipped, it changed while importing",
        ScheduleMutationOutcome.ActiveRun => "Skipped, it is running now",
        ScheduleMutationOutcome.ConstraintConflict => "Skipped, it conflicts with one already here",
        ScheduleMutationOutcome.Unavailable => "Skipped, the agent was unavailable",
        _ => response.Failure?.Message ?? "Skipped"
    };

    private static string Describe(ConnectionProfileWriteStatus status) => status switch
    {
        ConnectionProfileWriteStatus.NameConflict => "Skipped, that name is taken",
        ConnectionProfileWriteStatus.VersionConflict => "Skipped, it changed while importing",
        ConnectionProfileWriteStatus.ValidationFailed => "Skipped, it is not valid here",
        ConnectionProfileWriteStatus.Unavailable => "Skipped, the agent was unavailable",
        _ => "Skipped"
    };

    private static void ThrowIfFailed(StorageIpcFailure? failure)
    {
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message);
        }
    }
}
