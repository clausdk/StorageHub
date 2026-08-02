using StorageHub.Domain.Identifiers;

namespace StorageHub.Application.Connections;

public sealed record ConnectionProfileSearch(
    string? Text = null,
    string? FolderPath = null,
    string? Tag = null,
    ConnectionProviderKind? Provider = null,
    bool? IsFavorite = null,
    bool IncludeDisabled = false,
    bool IncludeDeleted = false,
    int Limit = 200)
{
    public int ValidatedLimit => Limit is >= 1 and <= 1_000
        ? Limit
        : throw new ArgumentOutOfRangeException(nameof(Limit), "The search limit must be between 1 and 1,000.");
}

public enum ConnectionProfileWriteStatus
{
    Succeeded = 1,
    NotFound = 2,
    VersionConflict = 3,
    NameConflict = 4,
    Deleted = 5
}

public sealed record ConnectionProfileWriteResult(
    ConnectionProfileWriteStatus Status,
    ConnectionProfile? Profile = null,
    long? ActualVersion = null);

public interface IConnectionProfileRepository
{
    ValueTask<ConnectionProfileWriteResult> CreateAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default);

    ValueTask<ConnectionProfile?> GetAsync(
        ConnectionProfileId id,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConnectionProfile>> SearchAsync(
        ConnectionProfileSearch search,
        CancellationToken cancellationToken = default);

    ValueTask<ConnectionProfileWriteResult> UpdateAsync(
        ConnectionProfile profile,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    ValueTask<ConnectionProfileWriteResult> SetEnabledAsync(
        ConnectionProfileId id,
        bool enabled,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    ValueTask<ConnectionProfileWriteResult> SoftDeleteAsync(
        ConnectionProfileId id,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}
