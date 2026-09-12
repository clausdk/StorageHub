namespace StorageHub.Desktop;

/// <summary>
/// Translates the identifiers inside an export file into the ones this machine ended up using.
///
/// Connections need this and sync tasks do not: creating a connection profile mints a fresh id on
/// the agent, while sync profiles and schedules accept the id the caller supplies. So a sync task
/// pointing at "the connection that was 1234" has to be told where 1234 landed.
/// </summary>
internal sealed class SettingsImportIdMap
{
    private readonly Dictionary<Guid, Guid> _connections = [];
    private readonly Dictionary<Guid, Guid> _syncProfiles = [];

    /// <summary>
    /// Records where an exported connection ended up. Called for every outcome that leaves a
    /// usable local profile — including a skipped one, because "skipped" means the connection is
    /// already here, and a sync task referring to it should point at the copy that exists rather
    /// than be orphaned.
    /// </summary>
    internal void RecordConnection(Guid exported, Guid local)
    {
        if (exported == Guid.Empty || local == Guid.Empty) return;
        _connections[exported] = local;
    }

    internal void RecordSyncProfile(Guid exported, Guid local)
    {
        if (exported == Guid.Empty || local == Guid.Empty) return;
        _syncProfiles[exported] = local;
    }

    /// <summary>
    /// Where an exported connection lives now. An id that was never remapped resolves to itself,
    /// which is what makes a same-machine restore work without any bookkeeping.
    /// </summary>
    internal bool TryResolveConnection(Guid exported, IReadOnlySet<Guid> existing, out Guid local)
    {
        if (_connections.TryGetValue(exported, out local)) return true;
        if (existing.Contains(exported))
        {
            local = exported;
            return true;
        }

        local = Guid.Empty;
        return false;
    }

    internal bool TryResolveSyncProfile(Guid exported, IReadOnlySet<Guid> existing, out Guid local)
    {
        if (_syncProfiles.TryGetValue(exported, out local)) return true;
        if (existing.Contains(exported))
        {
            local = exported;
            return true;
        }

        local = Guid.Empty;
        return false;
    }
}
