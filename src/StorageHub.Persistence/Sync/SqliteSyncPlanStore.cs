using Microsoft.Data.Sqlite;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Sync;
using StorageHub.Sync.Persistence;

namespace StorageHub.Persistence.Sync;

public sealed class SqliteSyncPlanStore(SingleWriterSqliteDatabase database) : ISyncPlanStore
{
    private readonly SingleWriterSqliteDatabase _database =
        database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<PersistedSyncPlan?> GetAsync(
        OperationPlanId planId,
        CancellationToken cancellationToken = default)
    {
        EnsurePlanId(planId);
        await using var connection = await _database
            .OpenReadConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(connection, null, planId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SyncPersistenceResult<PersistedSyncPlan>> PutAsync(
        ImmutableSyncPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.HasValidDigest)
        {
            throw new ArgumentException("The immutable sync plan digest is invalid.", nameof(plan));
        }

        if (plan.Operations.Length > SyncPersistenceUtilities.MaximumBaselineItems)
        {
            throw new ArgumentException("The sync plan contains too many operations.", nameof(plan));
        }

        await using var writer = await _database
            .AcquireWriterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await writer.Connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var existing = await ReadAsync(
                writer.Connection,
                transaction,
                plan.PlanId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncPersistenceResult<PersistedSyncPlan>(
                    existing.Plan.Digest == plan.Digest
                        ? SyncPersistenceMutationStatus.AlreadyApplied
                        : SyncPersistenceMutationStatus.Conflict,
                    existing.Plan.Digest == plan.Digest ? existing : null);
            }

            if (!await ProfileExistsAsync(
                    writer.Connection,
                    transaction,
                    plan.ProfileId,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new SyncPersistenceResult<PersistedSyncPlan>(
                    SyncPersistenceMutationStatus.NotFound,
                    null);
            }

            await InsertPlanAsync(writer.Connection, transaction, plan, cancellationToken).ConfigureAwait(false);
            foreach (var operation in plan.Operations)
            {
                await InsertOperationAsync(
                    writer.Connection,
                    transaction,
                    plan.PlanId,
                    operation,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return new SyncPersistenceResult<PersistedSyncPlan>(
                SyncPersistenceMutationStatus.Applied,
                new PersistedSyncPlan(plan));
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Preserve the primary insert or commit failure.
            }

            throw;
        }
    }

    private static async ValueTask<PersistedSyncPlan?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        OperationPlanId planId,
        CancellationToken cancellationToken)
    {
        SyncProfileId profileId;
        long baselineGeneration;
        string storedDigest;
        int operationCount;
        DateTimeOffset createdAtUtc;
        int digestSchemaVersion;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sync_profile_id, baseline_generation, plan_digest, operation_count, created_utc,
                       digest_schema_version
                FROM sync_plans
                WHERE plan_id = $planId;
                """;
            command.Parameters.AddWithValue("$planId", planId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (!SyncProfileId.TryParse(reader.GetString(0), out profileId))
            {
                throw new InvalidDataException("The persisted sync plan profile ID is invalid.");
            }

            baselineGeneration = reader.GetInt64(1);
            storedDigest = reader.GetString(2);
            operationCount = reader.GetInt32(3);
            createdAtUtc = SyncPersistenceUtilities.ParseTimestamp(reader.GetString(4), "plan creation time");
            digestSchemaVersion = reader.GetInt32(5);
        }

        var operations = new List<SyncPlanOperation>(operationCount);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT operation_order, operation_kind,
                       source_profile_id, source_root_identity, source_relative_path,
                       source_native_item_id, source_version_id, source_entity_tag,
                       destination_profile_id, destination_root_identity, destination_relative_path,
                       destination_native_item_id, destination_version_id, destination_entity_tag,
                       expected_length,
                       source_digest_algorithm, source_digest_value,
                       destination_digest_algorithm, destination_digest_value,
                       destination_existed
                FROM sync_plan_operations
                WHERE plan_id = $planId
                ORDER BY operation_order;
                """;
            command.Parameters.AddWithValue("$planId", planId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sequence = reader.GetInt32(0);
                var kind = SyncPersistenceUtilities.ParseEnum<SyncPlanOperationKind>(
                    reader.GetString(1),
                    "sync plan operation kind");
                var source = RestoreAddress(reader, 2);
                long? expectedLength = reader.IsDBNull(14) ? null : reader.GetInt64(14);
                operations.Add(kind switch
                {
                    SyncPlanOperationKind.Copy => SyncPlanOperation.Copy(
                        sequence,
                        source,
                        RestoreAddress(reader, 8),
                        expectedLength,
                        RestoreDigest(reader, 15),
                        RestoreDigest(reader, 17),
                        destinationExisted: reader.GetInt64(19) == 1),
                    SyncPlanOperationKind.Delete when expectedLength is null =>
                        SyncPlanOperation.Delete(sequence, source),
                    SyncPlanOperationKind.CreateDirectory when expectedLength is null =>
                        SyncPlanOperation.CreateDirectory(sequence, source),
                    _ => throw new InvalidDataException("The persisted sync plan operation is inconsistent.")
                });
            }
        }

        if (operations.Count != operationCount)
        {
            throw new InvalidDataException("The persisted sync plan operation count is inconsistent.");
        }

        var plan = ImmutableSyncPlan.Restore(
            planId,
            profileId,
            baselineGeneration,
            operations,
            createdAtUtc,
            digestSchemaVersion);
        if (!SyncPlanDigest.TryParse(storedDigest, out var digest) || digest != plan.Digest)
        {
            throw new InvalidDataException("The persisted sync plan digest is invalid.");
        }

        return new PersistedSyncPlan(plan);
    }

    private static StorageAddress RestoreAddress(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal) ||
            !ConnectionProfileId.TryParse(reader.GetString(ordinal), out var profileId))
        {
            throw new InvalidDataException("A persisted sync plan address has an invalid profile ID.");
        }

        var result = StorageAddress.Create(
            profileId,
            reader.GetString(ordinal + 1),
            reader.GetString(ordinal + 2),
            reader.IsDBNull(ordinal + 3) ? null : reader.GetString(ordinal + 3),
            reader.IsDBNull(ordinal + 4) ? null : reader.GetString(ordinal + 4),
            reader.IsDBNull(ordinal + 5) ? null : reader.GetString(ordinal + 5));
        return result.IsSuccess
            ? result.Value
            : throw new InvalidDataException("A persisted sync plan address is invalid or non-canonical.");
    }

    private static async Task InsertPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImmutableSyncPlan plan,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_plans
            (plan_id, sync_profile_id, baseline_generation, plan_digest, operation_count, created_utc,
             digest_schema_version)
            VALUES ($planId, $profileId, $generation, $digest, $count, $createdAt, $digestSchemaVersion);
            """;
        command.Parameters.AddWithValue("$planId", plan.PlanId.ToString());
        command.Parameters.AddWithValue("$profileId", plan.ProfileId.ToString());
        command.Parameters.AddWithValue("$generation", plan.BaselineGeneration);
        command.Parameters.AddWithValue("$digest", plan.Digest.Sha256Hex);
        command.Parameters.AddWithValue("$count", plan.Operations.Length);
        command.Parameters.AddWithValue("$createdAt", SyncPersistenceUtilities.FormatTimestamp(plan.CreatedAtUtc));
        command.Parameters.AddWithValue("$digestSchemaVersion", plan.DigestSchemaVersion);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        OperationPlanId planId,
        SyncPlanOperation operation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_plan_operations
            (
                plan_id, operation_order, operation_kind,
                source_profile_id, source_root_identity, source_relative_path,
                source_native_item_id, source_version_id, source_entity_tag,
                destination_profile_id, destination_root_identity, destination_relative_path,
                destination_native_item_id, destination_version_id, destination_entity_tag,
                expected_length,
                source_digest_algorithm, source_digest_value,
                destination_digest_algorithm, destination_digest_value,
                destination_existed
            )
            VALUES
            (
                $planId, $order, $kind,
                $sourceProfile, $sourceRoot, $sourcePath, $sourceNative, $sourceVersion, $sourceEntityTag,
                $destinationProfile, $destinationRoot, $destinationPath,
                $destinationNative, $destinationVersion, $destinationEntityTag, $expectedLength,
                $sourceDigestAlgorithm, $sourceDigestValue,
                $destinationDigestAlgorithm, $destinationDigestValue,
                $destinationExisted
            );
            """;
        command.Parameters.AddWithValue("$planId", planId.ToString());
        command.Parameters.AddWithValue("$order", operation.Sequence);
        command.Parameters.AddWithValue("$kind", operation.Kind.ToString());
        AddAddressParameters(command, "source", operation.SourceOrTarget);
        AddAddressParameters(command, "destination", operation.Destination);
        command.Parameters.AddWithValue("$expectedLength", (object?)operation.ExpectedLength ?? DBNull.Value);
        AddDigestParameters(command, "source", operation.SourceDigest);
        AddDigestParameters(command, "destination", operation.DestinationDigest);
        command.Parameters.AddWithValue("$destinationExisted", operation.DestinationExisted ? 1 : 0);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddAddressParameters(
        SqliteCommand command,
        string prefix,
        StorageAddress? address)
    {
        command.Parameters.AddWithValue($"${prefix}Profile", (object?)address?.ProfileId.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}Root", (object?)address?.RootIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}Path", (object?)address?.CanonicalRelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}Native", (object?)address?.NativeItemId ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}Version", (object?)address?.VersionId ?? DBNull.Value);
        command.Parameters.AddWithValue($"${prefix}EntityTag", (object?)address?.EntityTag ?? DBNull.Value);
    }

    private static PortableContentDigest? RestoreDigest(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal) != reader.IsDBNull(ordinal + 1))
        {
            throw new InvalidDataException("A persisted portable sync digest is incomplete.");
        }

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return PortableContentDigest.Parse(reader.GetString(ordinal), reader.GetString(ordinal + 1));
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("A persisted portable sync digest is invalid.", error);
        }
    }

    private static void AddDigestParameters(
        SqliteCommand command,
        string prefix,
        PortableContentDigest? digest)
    {
        command.Parameters.AddWithValue(
            $"${prefix}DigestAlgorithm",
            (object?)digest?.AlgorithmName ?? DBNull.Value);
        command.Parameters.AddWithValue(
            $"${prefix}DigestValue",
            (object?)digest?.Value ?? DBNull.Value);
    }

    private static async ValueTask<bool> ProfileExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncProfileId profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sync_profiles WHERE sync_profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", profileId.ToString());
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void EnsurePlanId(OperationPlanId planId)
    {
        if (planId.IsEmpty)
        {
            throw new ArgumentException("A sync plan ID is required.", nameof(planId));
        }
    }
}
