using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Storage.Abstractions;
using StorageHub.Sync.Persistence;

namespace StorageHub.Sync;

/// <summary>Owns a root-scoped runtime connection and its provider session.</summary>
public interface ISyncEndpointConnection : IAsyncDisposable
{
    IStorageEndpointSession Session { get; }
}

/// <summary>The caller owns and must dispose a successfully opened runtime connection.</summary>
public interface ISyncEndpointConnector
{
    ValueTask<StorageResult<ISyncEndpointConnection>> OpenAsync(
        ConnectionProfileId profileId,
        CancellationToken cancellationToken = default);
}

public enum SyncPreviewTrigger
{
    Manual = 0,
    Scheduled = 1,
}

public sealed record SyncPreviewResult(
    SyncPreviewRecord Preview,
    ImmutableSyncPlan Plan,
    IReadOnlyList<SyncConflictRecord> Conflicts);

public interface ISyncOrchestrationService
{
    ValueTask<StorageResult<SyncPreviewResult>> GeneratePreviewAsync(
        SyncProfileId profileId,
        SyncPreviewTrigger trigger = SyncPreviewTrigger.Manual,
        string? triggerIdempotencyKey = null,
        CancellationToken cancellationToken = default);

    ValueTask<StorageResult<SyncPreviewRecord>> ApproveAndDispatchAsync(
        SyncRunId syncRunId,
        long expectedRevision,
        string approvalSha256,
        CancellationToken cancellationToken = default);
}
