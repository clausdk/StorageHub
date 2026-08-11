using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StorageHub.Domain.Identifiers;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;
using StorageHub.Transfers;

namespace StorageHub.Persistence.Sync;

public sealed class SqliteSyncProfileRepository : ISyncProfileRepository
{
    private readonly SingleWriterSqliteDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteSyncProfileRepository(
        SingleWriterSqliteDatabase database,
        TimeProvider? timeProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<SyncProfile?> GetAsync(
        SyncProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        EnsureId(profileId);
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(connection, null, profileId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SyncProfile>> ListAsync(
        bool includeDisabled = true,
        int maximumCount = 1_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 10_000);
        await using var connection = await _database.OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Projection}
            FROM sync_profiles
            WHERE $includeDisabled = 1 OR enabled = 1
            ORDER BY display_name COLLATE NOCASE, sync_profile_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$includeDisabled", includeDisabled ? 1 : 0);
        command.Parameters.AddWithValue("$limit", maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var profiles = new List<SyncProfile>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async ValueTask<SyncProfileWriteResult> CreateAsync(
        SyncProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Revision != 1 || profile.CreatedAtUtc != profile.UpdatedAtUtc)
        {
            throw new ArgumentException(
                "A new sync profile must be at revision 1 with matching creation/update times.",
                nameof(profile));
        }

        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var existing = await ReadAsync(
                writer.Connection,
                transaction,
                profile.ProfileId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Equivalent(existing, profile)
                    ? new SyncProfileWriteResult(
                        SyncProfileWriteStatus.AlreadyApplied,
                        existing,
                        existing.Revision)
                    : new SyncProfileWriteResult(
                        SyncProfileWriteStatus.RevisionConflict,
                        ActualRevision: existing.Revision);
            }

            await InsertAsync(writer.Connection, transaction, profile, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncProfileWriteResult(
                SyncProfileWriteStatus.Succeeded,
                profile,
                profile.Revision);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return new SyncProfileWriteResult(SyncProfileWriteStatus.ConstraintConflict);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SyncProfileWriteResult> UpdateAsync(
        SyncProfile profile,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRevision);
        if (profile.Revision != expectedRevision)
        {
            throw new ArgumentException(
                "The profile document revision must match the expected revision.",
                nameof(profile));
        }

        await using var writer = await _database.AcquireWriterAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = writer.Connection.BeginTransaction(deferred: false);
        try
        {
            var current = await ReadAsync(
                writer.Connection,
                transaction,
                profile.ProfileId,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncProfileWriteResult(SyncProfileWriteStatus.NotFound);
            }

            if (current.Revision != expectedRevision)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Equivalent(current, profile)
                    ? new SyncProfileWriteResult(
                        SyncProfileWriteStatus.AlreadyApplied,
                        current,
                        current.Revision)
                    : new SyncProfileWriteResult(
                        SyncProfileWriteStatus.RevisionConflict,
                        ActualRevision: current.Revision);
            }

            var updated = new SyncProfile(
                profile.ProfileId,
                profile.DisplayName,
                profile.LeftConnectionProfileId,
                profile.LeftRoot,
                profile.RightConnectionProfileId,
                profile.RightRoot,
                profile.Direction,
                profile.DeletionMode,
                profile.ConflictPolicy,
                profile.DeletionSafetyPolicy,
                profile.TransferOptions,
                profile.Enabled,
                checked(expectedRevision + 1),
                current.CreatedAtUtc,
                _timeProvider.GetUtcNow(),
                profile.FilterPolicy,
                profile.Behavior);
            var policyChanged = !StringComparer.Ordinal.Equals(
                current.PolicySha256,
                updated.PolicySha256);
            var changed = await UpdateCoreAsync(
                writer.Connection,
                transaction,
                updated,
                expectedRevision,
                policyChanged,
                cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncProfileWriteResult(SyncProfileWriteStatus.RevisionConflict);
            }

            if (policyChanged)
            {
                await DeleteBaselineAsync(
                    writer.Connection,
                    transaction,
                    updated.ProfileId,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncProfileWriteResult(
                SyncProfileWriteStatus.Succeeded,
                updated,
                updated.Revision);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            return new SyncProfileWriteResult(SyncProfileWriteStatus.ConstraintConflict);
        }
        catch
        {
            await TryRollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<SyncProfile?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SyncProfileId profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Projection} FROM sync_profiles WHERE sync_profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;
    }

    private static SyncProfile ReadProfile(SqliteDataReader reader)
    {
        if (!SyncProfileId.TryParse(reader.GetString(0), out var id) ||
            !ConnectionProfileId.TryParse(reader.GetString(2), out var left) ||
            !ConnectionProfileId.TryParse(reader.GetString(4), out var right))
        {
            throw new InvalidDataException("A persisted sync profile identifier is invalid.");
        }

        var profile = new SyncProfile(
            id,
            reader.GetString(1),
            left,
            reader.GetString(3),
            right,
            reader.GetString(5),
            ParseDirection(reader.GetString(6)),
            ParseDeletionMode(reader.GetString(7)),
            ParseConflictPolicy(reader.GetString(8)),
            new DeletionSafetyPolicy(
                reader.GetInt32(12),
                decimal.Parse(reader.GetString(13), NumberStyles.Number, CultureInfo.InvariantCulture)),
            new TransferExecutionOptions(
                reader.GetInt64(14) == 1,
                reader.GetInt32(15),
                reader.GetInt64(22) == 1),
            reader.GetInt64(10) == 1,
            reader.GetInt64(11),
            SyncPersistenceUtilities.ParseTimestamp(reader.GetString(16), "sync profile creation time"),
            SyncPersistenceUtilities.ParseTimestamp(reader.GetString(17), "sync profile update time"),
            new SyncPathFilterPolicy(
                JsonSerializer.Deserialize<string[]>(reader.GetString(19)) ?? [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(20)) ?? [],
                reader.GetInt64(21) == 1),
            ParseBehavior(reader.GetString(18)));

        return profile;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfile profile,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_profiles
            (sync_profile_id, display_name, left_profile_id, right_profile_id, left_root, right_root,
             direction, deletion_policy, conflict_policy, policy_hash, enabled,
             profile_revision, maximum_deletion_count, maximum_deletion_percentage,
             transfer_overwrite, transfer_buffer_size, created_utc, updated_utc,
             behavior, include_globs_json, exclude_globs_json, include_hidden_files,
             allow_non_atomic_destination_writes)
            VALUES
            ($id, $name, $leftId, $rightId, $leftRoot, $rightRoot,
             $direction, $deletion, $conflict, $hash, $enabled,
             $revision, $maximumDeletes, $maximumPercentage,
             $overwrite, $bufferSize, $created, $updated,
             $behavior, $includeGlobs, $excludeGlobs, $includeHidden, $allowNonAtomicWrites);
            """;
        Bind(command, profile);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfile profile,
        long expectedRevision,
        bool resetBaseline,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sync_profiles
            SET display_name = $name,
                left_profile_id = $leftId,
                right_profile_id = $rightId,
                left_root = $leftRoot,
                right_root = $rightRoot,
                direction = $direction,
                deletion_policy = $deletion,
                conflict_policy = $conflict,
                policy_hash = $hash,
                enabled = $enabled,
                profile_revision = $revision,
                maximum_deletion_count = $maximumDeletes,
                maximum_deletion_percentage = $maximumPercentage,
                transfer_overwrite = $overwrite,
                transfer_buffer_size = $bufferSize,
                behavior = $behavior,
                include_globs_json = $includeGlobs,
                exclude_globs_json = $excludeGlobs,
                include_hidden_files = $includeHidden,
                allow_non_atomic_destination_writes = $allowNonAtomicWrites,
                updated_utc = $updated,
                baseline_generation = CASE WHEN $reset = 1 THEN 0 ELSE baseline_generation END,
                baseline_revision = CASE WHEN $reset = 1 THEN baseline_revision + 1 ELSE baseline_revision END,
                baseline_sha256 = CASE WHEN $reset = 1 THEN NULL ELSE baseline_sha256 END,
                baseline_updated_utc = CASE WHEN $reset = 1 THEN NULL ELSE baseline_updated_utc END
            WHERE sync_profile_id = $id AND profile_revision = $expectedRevision;
            """;
        Bind(command, profile);
        command.Parameters.AddWithValue("$reset", resetBaseline ? 1 : 0);
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, SyncProfile profile)
    {
        command.Parameters.AddWithValue("$id", profile.ProfileId.ToString());
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$leftId", profile.LeftConnectionProfileId.ToString());
        command.Parameters.AddWithValue("$rightId", profile.RightConnectionProfileId.ToString());
        command.Parameters.AddWithValue("$leftRoot", profile.LeftRoot);
        command.Parameters.AddWithValue("$rightRoot", profile.RightRoot);
        command.Parameters.AddWithValue("$direction", FormatDirection(profile.Direction));
        command.Parameters.AddWithValue("$deletion", FormatDeletionMode(profile.DeletionMode));
        command.Parameters.AddWithValue("$conflict", profile.ConflictPolicy == SyncConflictPolicy.KeepBoth ? "keep-both" : "block");
        command.Parameters.AddWithValue("$hash", profile.PolicySha256);
        command.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$revision", profile.Revision);
        command.Parameters.AddWithValue("$maximumDeletes", profile.DeletionSafetyPolicy.MaximumDeletionCount);
        command.Parameters.AddWithValue(
            "$maximumPercentage",
            profile.DeletionSafetyPolicy.MaximumDeletionPercentage.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$overwrite", profile.TransferOptions.Overwrite ? 1 : 0);
        command.Parameters.AddWithValue("$bufferSize", profile.TransferOptions.BufferSize);
        command.Parameters.AddWithValue("$created", SyncPersistenceUtilities.FormatTimestamp(profile.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", SyncPersistenceUtilities.FormatTimestamp(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$behavior", FormatBehavior(profile.Behavior));
        command.Parameters.AddWithValue("$includeGlobs", JsonSerializer.Serialize(profile.FilterPolicy.IncludeGlobs));
        command.Parameters.AddWithValue("$excludeGlobs", JsonSerializer.Serialize(profile.FilterPolicy.ExcludeGlobs));
        command.Parameters.AddWithValue("$includeHidden", profile.FilterPolicy.IncludeHiddenFiles ? 1 : 0);
        command.Parameters.AddWithValue(
            "$allowNonAtomicWrites",
            profile.TransferOptions.AllowNonAtomicDestinationWrites ? 1 : 0);
    }

    private static async Task DeleteBaselineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfileId profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM sync_item_state WHERE sync_profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId.ToString());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool Equivalent(SyncProfile left, SyncProfile right) =>
        left.ProfileId == right.ProfileId &&
        StringComparer.Ordinal.Equals(left.DisplayName, right.DisplayName) &&
        StringComparer.Ordinal.Equals(left.PolicySha256, right.PolicySha256) &&
        left.Enabled == right.Enabled;

    private static string FormatDirection(SyncDirection value) => value switch
    {
        SyncDirection.LeftToRight => "left-to-right",
        SyncDirection.RightToLeft => "right-to-left",
        SyncDirection.TwoWay => "two-way",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SyncDirection ParseDirection(string value) => value switch
    {
        "left-to-right" => SyncDirection.LeftToRight,
        "right-to-left" => SyncDirection.RightToLeft,
        "two-way" or "bidirectional" => SyncDirection.TwoWay,
        _ => throw new InvalidDataException("The persisted sync direction is invalid."),
    };

    private static string FormatDeletionMode(SyncDeletionMode value) => value switch
    {
        SyncDeletionMode.Disabled => "disabled",
        SyncDeletionMode.Mirror => "mirror",
        SyncDeletionMode.Propagate => "propagate",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SyncDeletionMode ParseDeletionMode(string value) => value switch
    {
        "disabled" => SyncDeletionMode.Disabled,
        "mirror" => SyncDeletionMode.Mirror,
        "propagate" => SyncDeletionMode.Propagate,
        _ => throw new InvalidDataException("The persisted sync deletion mode is invalid."),
    };

    private static SyncConflictPolicy ParseConflictPolicy(string value) => value switch
    {
        "block" => SyncConflictPolicy.Block,
        "keep-both" => SyncConflictPolicy.KeepBoth,
        _ => throw new InvalidDataException("The persisted sync conflict policy is invalid."),
    };

    private static string FormatBehavior(SyncBehavior value) => value switch
    {
        SyncBehavior.CopyNewFilesAToB => "copy-new-a-to-b",
        SyncBehavior.UpdateAToB => "update-a-to-b",
        SyncBehavior.MirrorAToB => "mirror-a-to-b",
        SyncBehavior.CopyNewFilesBToA => "copy-new-b-to-a",
        SyncBehavior.UpdateBToA => "update-b-to-a",
        SyncBehavior.MirrorBToA => "mirror-b-to-a",
        SyncBehavior.TwoWaySync => "two-way",
        SyncBehavior.TwoWayWithDeletionPropagation => "two-way-delete",
        SyncBehavior.CompareOnly => "compare-only",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SyncBehavior ParseBehavior(string value) => value switch
    {
        "copy-new-a-to-b" => SyncBehavior.CopyNewFilesAToB,
        "update-a-to-b" => SyncBehavior.UpdateAToB,
        "mirror-a-to-b" => SyncBehavior.MirrorAToB,
        "copy-new-b-to-a" => SyncBehavior.CopyNewFilesBToA,
        "update-b-to-a" => SyncBehavior.UpdateBToA,
        "mirror-b-to-a" => SyncBehavior.MirrorBToA,
        "two-way" => SyncBehavior.TwoWaySync,
        "two-way-delete" => SyncBehavior.TwoWayWithDeletionPropagation,
        "compare-only" => SyncBehavior.CompareOnly,
        _ => throw new InvalidDataException("The persisted sync behavior is invalid."),
    };

    private static void EnsureId(SyncProfileId profileId)
    {
        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }
    }

    private static async Task TryRollbackAsync(SqliteTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Preserve the primary write failure.
        }
    }

    private const string Projection = """
        sync_profile_id, display_name, left_profile_id, left_root, right_profile_id, right_root,
        direction, deletion_policy, conflict_policy, policy_hash, enabled, profile_revision,
        maximum_deletion_count, maximum_deletion_percentage, transfer_overwrite,
        transfer_buffer_size, created_utc, updated_utc, behavior, include_globs_json,
        exclude_globs_json, include_hidden_files, allow_non_atomic_destination_writes
        """;
}
