using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using StorageHub.Domain.Identifiers;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;

namespace StorageHub.Persistence.Sync;

public sealed class SqliteSyncBaselineStore(SingleWriterSqliteDatabase database) : ISyncBaselineStore
{
    private readonly SingleWriterSqliteDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<SyncBaselineSnapshot?> GetAsync(
        SyncProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureProfileId(profileId);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadSnapshotAsync(connection, null, profileId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SyncPersistenceResult<SyncBaselineSnapshot>> ReplaceAsync(
        SyncBaselineReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureProfileId(request.ProfileId);
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Generation);
        SyncPersistenceUtilities.ValidateUtc(request.UpdatedAtUtc, nameof(request));
        var digest = SyncPersistenceUtilities.ComputeBaselineDigest(request.Items);

        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await writer.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var current = await ReadSnapshotAsync(
                writer.Connection,
                transaction,
                request.ProfileId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.NotFound);
            }

            if (current.Generation == request.Generation &&
                string.Equals(current.Sha256Digest, digest, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncPersistenceResult<SyncBaselineSnapshot>(
                    SyncPersistenceMutationStatus.AlreadyApplied,
                    current);
            }

            if (current.Revision != request.ExpectedRevision || request.Generation <= current.Generation)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(SyncPersistenceMutationStatus.Conflict);
            }

            var nextRevision = checked(current.Revision + 1);
            await UpdateProfileAsync(
                writer.Connection,
                transaction,
                request,
                digest,
                nextRevision,
                cancellationToken).ConfigureAwait(false);
            await DeleteItemsAsync(
                writer.Connection,
                transaction,
                request.ProfileId,
                cancellationToken).ConfigureAwait(false);
            foreach (var (path, observation) in request.Items.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                await InsertItemAsync(
                    writer.Connection,
                    transaction,
                    request,
                    path,
                    observation,
                    nextRevision,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<SyncBaselineSnapshot>(
                SyncPersistenceMutationStatus.Applied,
                CreateSnapshot(request, digest, nextRevision));
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The transaction was already completed; preserve the original failure.
            }

            throw;
        }
    }

    internal static async ValueTask<SyncBaselineSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SyncProfileId profileId,
        CancellationToken cancellationToken)
    {
        long generation;
        long revision;
        string? storedDigest;
        DateTimeOffset? updatedAt;
        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText = """
                SELECT baseline_generation, baseline_revision, baseline_sha256, baseline_updated_utc
                FROM sync_profiles
                WHERE sync_profile_id = $profileId;
                """;
            profile.Parameters.AddWithValue("$profileId", profileId.ToString());
            await using var reader = await profile.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            generation = reader.GetInt64(0);
            revision = reader.GetInt64(1);
            storedDigest = reader.IsDBNull(2) ? null : reader.GetString(2);
            updatedAt = reader.IsDBNull(3)
                ? null
                : SyncPersistenceUtilities.ParseTimestamp(reader.GetString(3), "baseline update time");
        }

        var items = new Dictionary<string, SyncBaselineObservation>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT relative_path, baseline_exists, baseline_length,
                       baseline_digest_algorithm, baseline_digest_value,
                       baseline_left_version_id, baseline_right_version_id,
                       baseline_generation, record_revision, baseline_updated_utc
                FROM sync_item_state
                WHERE sync_profile_id = $profileId
                ORDER BY relative_path COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$profileId", profileId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                SyncPersistenceUtilities.ValidateRelativePath(path, "persisted relative path");
                if (reader.GetInt64(7) != generation || reader.GetInt64(8) != revision)
                {
                    throw new InvalidDataException("The sync baseline rows do not match their profile revision.");
                }

                var rowUpdatedAt = SyncPersistenceUtilities.ParseTimestamp(
                    reader.GetString(9),
                    "baseline item update time");
                if (updatedAt is not null && rowUpdatedAt != updatedAt)
                {
                    throw new InvalidDataException("The sync baseline item timestamp is inconsistent.");
                }

                var exists = reader.GetInt64(1) == 1;
                var digestAlgorithmMissing = reader.IsDBNull(3);
                var digestValueMissing = reader.IsDBNull(4);
                if (digestAlgorithmMissing != digestValueMissing ||
                    (!exists &&
                     (reader.GetInt64(2) != 0 || !digestAlgorithmMissing ||
                      !reader.IsDBNull(5) || !reader.IsDBNull(6))))
                {
                    throw new InvalidDataException("A persisted sync baseline observation is inconsistent.");
                }

                var observation = exists
                    ? SyncBaselineObservation.Present(
                        reader.GetInt64(2),
                        digestAlgorithmMissing
                            ? null
                            : new ContentDigest(reader.GetString(3), reader.GetString(4)),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6))
                    : SyncBaselineObservation.Missing;
                if (!items.TryAdd(path, observation))
                {
                    throw new InvalidDataException("The persisted sync baseline contains a duplicate path.");
                }
            }
        }

        var computedDigest = SyncPersistenceUtilities.ComputeBaselineDigest(items);
        if (storedDigest is not null && !string.Equals(storedDigest, computedDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The persisted sync baseline digest does not match its item rows.");
        }

        return new SyncBaselineSnapshot(
            profileId,
            generation,
            revision,
            new ReadOnlyDictionary<string, SyncBaselineObservation>(items),
            computedDigest,
            updatedAt ?? DateTimeOffset.UnixEpoch);
    }

    internal static async Task UpdateProfileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncBaselineReplaceRequest request,
        string digest,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sync_profiles
            SET baseline_generation = $generation,
                baseline_revision = $nextRevision,
                baseline_sha256 = $digest,
                baseline_updated_utc = $updatedAt
            WHERE sync_profile_id = $profileId AND baseline_revision = $expectedRevision;
            """;
        command.Parameters.AddWithValue("$generation", request.Generation);
        command.Parameters.AddWithValue("$nextRevision", nextRevision);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$updatedAt", SyncPersistenceUtilities.FormatTimestamp(request.UpdatedAtUtc));
        command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
        command.Parameters.AddWithValue("$expectedRevision", request.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException("The sync baseline revision changed inside the writer transaction.");
        }
    }

    internal static async Task DeleteItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfileId profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM sync_item_state WHERE sync_profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId.ToString());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncBaselineReplaceRequest request,
        string path,
        SyncBaselineObservation observation,
        long revision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_item_state
            (
                sync_profile_id, relative_path, baseline_generation,
                left_identity, right_identity, left_size, right_size,
                left_hash, right_hash, tombstone_side,
                baseline_exists, baseline_length, baseline_digest_algorithm,
                baseline_digest_value, baseline_left_version_id, baseline_right_version_id,
                record_revision, baseline_updated_utc
            )
            VALUES
            (
                $profileId, $path, $generation,
                $leftVersion, $rightVersion, $leftSize, $rightSize,
                $digestValue, $digestValue, $tombstone,
                $exists, $length, $digestAlgorithm,
                $digestValue, $leftVersion, $rightVersion,
                $revision, $updatedAt
            );
            """;
        command.Parameters.AddWithValue("$profileId", request.ProfileId.ToString());
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$generation", request.Generation);
        command.Parameters.AddWithValue("$leftVersion", (object?)observation.LeftVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$rightVersion", (object?)observation.RightVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$leftSize", observation.Exists ? observation.Length : DBNull.Value);
        command.Parameters.AddWithValue("$rightSize", observation.Exists ? observation.Length : DBNull.Value);
        command.Parameters.AddWithValue("$digestAlgorithm", (object?)observation.Digest?.Algorithm ?? DBNull.Value);
        command.Parameters.AddWithValue("$digestValue", (object?)observation.Digest?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$tombstone", observation.Exists ? DBNull.Value : "both");
        command.Parameters.AddWithValue("$exists", observation.Exists ? 1 : 0);
        command.Parameters.AddWithValue("$length", observation.Length);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updatedAt", SyncPersistenceUtilities.FormatTimestamp(request.UpdatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static SyncBaselineSnapshot CreateSnapshot(
        SyncBaselineReplaceRequest request,
        string digest,
        long revision) => new(
        request.ProfileId,
        request.Generation,
        revision,
        new ReadOnlyDictionary<string, SyncBaselineObservation>(
            new Dictionary<string, SyncBaselineObservation>(request.Items, StringComparer.Ordinal)),
        digest,
        request.UpdatedAtUtc);

    private static void EnsureProfileId(SyncProfileId profileId)
    {
        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }
    }

    private static SyncPersistenceResult<SyncBaselineSnapshot> Rejected(
        SyncPersistenceMutationStatus status) => new(status, null);
}
