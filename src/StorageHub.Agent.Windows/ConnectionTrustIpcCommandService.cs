using StorageHub.Agent.Ipc;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence;
using StorageHub.Persistence.Connections;
using StorageHub.Persistence.Trust;
using StorageHub.Security;

namespace StorageHub.Agent.Windows;

/// <summary>Profile-revision-bound IPC for authoritative server identity decisions.</summary>
public sealed class ConnectionTrustIpcCommandService : IAgentIpcCommandHandler
{
    private const string FingerprintAlgorithm = "SHA256";
    private readonly IConnectionProfileRepository _profiles;
    private readonly ITrustManagementStore _trust;
    private readonly TimeProvider _timeProvider;

    public ConnectionTrustIpcCommandService(
        IConnectionProfileRepository profiles,
        ITrustManagementStore trust,
        TimeProvider? timeProvider = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ConnectionTrustIpcCommandService(
        SqliteDatabaseOptions databaseOptions,
        TimeProvider? timeProvider = null)
        : this(
            new SqliteConnectionProfileRepository(databaseOptions, timeProvider),
            new SqliteTrustStore(new SingleWriterSqliteDatabase(databaseOptions)),
            timeProvider)
    {
    }

    public bool CanHandle(string messageType) => messageType is
        ConnectionTrustIpcMessageTypes.GetRequest or
        ConnectionTrustIpcMessageTypes.DecideRequest or
        ConnectionTrustIpcMessageTypes.RolloverRequest;

    public ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            ConnectionTrustIpcMessageTypes.GetRequest => GetAsync(request, cancellationToken),
            ConnectionTrustIpcMessageTypes.DecideRequest => DecideAsync(request, cancellationToken),
            ConnectionTrustIpcMessageTypes.RolloverRequest => RolloverAsync(request, cancellationToken),
            _ => ValueTask.FromResult(AgentIpcCommandResponse.Error(
                "ipc.message.unsupported",
                "The requested IPC operation is not supported by this agent version."))
        };
    }

    private async ValueTask<AgentIpcCommandResponse> GetAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionTrustGetRequest>();
        if (!ConnectionTrustIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return GetFailure("connection.trust.request.invalid", StorageIpcFailureCategory.Validation,
                "The trust request was invalid or outside the negotiated bounds.");
        }

        try
        {
            var resolved = await ResolveAsync(
                request.ConnectionId,
                request.ExpectedProfileVersion,
                cancellationToken).ConfigureAwait(false);
            return resolved.Failure is not null
                ? GetFailure(resolved.Failure.Code, resolved.Failure.Category, resolved.Failure.Message)
                : AgentIpcCommandResponse.Create(
                    ConnectionTrustIpcMessageTypes.GetResponse,
                    new ConnectionTrustGetResponse(
                        ConnectionTrustIpcContract.CurrentVersion,
                        await SnapshotAsync(resolved.Profile!, resolved.Target!, cancellationToken)
                            .ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return GetFailure("connection.trust.unavailable", StorageIpcFailureCategory.Unavailable,
                "Server trust records are temporarily unavailable.", isTransient: true);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> DecideAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionTrustDecisionRequest>();
        if (!ConnectionTrustIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return MutationFailure(
                ConnectionTrustIpcMessageTypes.DecideResponse,
                ConnectionTrustMutationStatus.ValidationFailed,
                "connection.trust.request.invalid",
                StorageIpcFailureCategory.Validation,
                "The trust decision was invalid or outside the negotiated bounds.");
        }

        try
        {
            var resolved = await ResolveAsync(
                request.ConnectionId,
                request.ExpectedProfileVersion,
                cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null)
            {
                return MapResolvedFailure(ConnectionTrustIpcMessageTypes.DecideResponse, resolved.Failure);
            }

            var target = resolved.Target!;
            var records = await _trust.FindAsync(
                target.ArtifactKind,
                target.CanonicalHost,
                target.Port,
                cancellationToken).ConfigureAwait(false);
            var existing = ResolveExisting(records, request.ExistingTrustId, request.ExpectedTrustVersion);
            if (request.ExistingTrustId is not null && existing is null)
            {
                return MutationFailure(
                    ConnectionTrustIpcMessageTypes.DecideResponse,
                    ConnectionTrustMutationStatus.VersionConflict,
                    "connection.trust.version_conflict",
                    StorageIpcFailureCategory.Conflict,
                    "The trust record changed after it was loaded. Reload before changing it.");
            }

            var now = UtcNow();
            var fingerprint = request.Sha256Fingerprint.Trim();
            if (existing is null && records.Any(record =>
                EquivalentFingerprint(record.Sha256Fingerprint, fingerprint)))
            {
                return MutationFailure(
                    ConnectionTrustIpcMessageTypes.DecideResponse,
                    ConnectionTrustMutationStatus.VersionConflict,
                    "connection.trust.existing_record_required",
                    StorageIpcFailureCategory.Conflict,
                    "This server identity already has a trust record. Reload before changing it.");
            }

            if (existing is not null &&
                !EquivalentFingerprint(existing.Sha256Fingerprint, fingerprint))
            {
                return MutationFailure(
                    ConnectionTrustIpcMessageTypes.DecideResponse,
                    ConnectionTrustMutationStatus.ValidationFailed,
                    "connection.trust.identity_mismatch",
                    StorageIpcFailureCategory.Validation,
                    "A trust decision cannot change the identity of an existing record.");
            }

            var record = existing is null
                ? new TrustRecord(
                    TrustRecordId.New().Value.ToString("N"),
                    target.ArtifactKind,
                    target.CanonicalHost,
                    target.Port,
                    FingerprintAlgorithm,
                    fingerprint,
                    MapDecision(request.Decision),
                    TrustDecisionSource.UserVerified,
                    now,
                    now,
                    ExpiresUtc: null,
                    PreviousFingerprint: null,
                    Version: 1)
                : existing with
                {
                    Decision = MapDecision(request.Decision),
                    LastSeenUtc = now,
                    ExpiresUtc = request.Decision == ConnectionTrustDecision.Trusted
                        ? null
                        : existing.ExpiresUtc,
                    Version = checked(existing.Version + 1)
                };
            await _trust.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            return await MutationSuccessAsync(
                ConnectionTrustIpcMessageTypes.DecideResponse,
                resolved.Profile!,
                target,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TrustRecordConcurrencyException)
        {
            return MutationFailure(
                ConnectionTrustIpcMessageTypes.DecideResponse,
                ConnectionTrustMutationStatus.VersionConflict,
                "connection.trust.version_conflict",
                StorageIpcFailureCategory.Conflict,
                "The trust record changed after it was loaded. Reload before changing it.");
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            return MutationFailure(
                ConnectionTrustIpcMessageTypes.DecideResponse,
                ConnectionTrustMutationStatus.ValidationFailed,
                "connection.trust.validation_failed",
                StorageIpcFailureCategory.Validation,
                "The server identity fingerprint or decision is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure(
                ConnectionTrustIpcMessageTypes.DecideResponse,
                ConnectionTrustMutationStatus.Unavailable,
                "connection.trust.unavailable",
                StorageIpcFailureCategory.Unavailable,
                "The trust decision could not be saved.",
                isTransient: true);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> RolloverAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ConnectionTrustRolloverRequest>();
        if (!ConnectionTrustIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return MutationFailure(
                ConnectionTrustIpcMessageTypes.RolloverResponse,
                ConnectionTrustMutationStatus.ValidationFailed,
                "connection.trust.request.invalid",
                StorageIpcFailureCategory.Validation,
                "The trust rollover was invalid or outside the negotiated bounds.");
        }

        try
        {
            var resolved = await ResolveAsync(
                request.ConnectionId,
                request.ExpectedProfileVersion,
                cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null)
            {
                return MapResolvedFailure(ConnectionTrustIpcMessageTypes.RolloverResponse, resolved.Failure);
            }

            var target = resolved.Target!;
            var records = await _trust.FindAsync(
                target.ArtifactKind,
                target.CanonicalHost,
                target.Port,
                cancellationToken).ConfigureAwait(false);
            var previous = ResolveExisting(records, request.PreviousTrustId, request.ExpectedPreviousTrustVersion);
            var now = UtcNow();
            if (previous is null || previous.Decision != TrustDecision.Trusted ||
                previous.ExpiresUtc is { } expiry && expiry <= now)
            {
                return MutationFailure(
                    ConnectionTrustIpcMessageTypes.RolloverResponse,
                    ConnectionTrustMutationStatus.VersionConflict,
                    "connection.trust.rollover_source_changed",
                    StorageIpcFailureCategory.Conflict,
                    "The trusted rollover source changed or is no longer active. Reload before continuing.");
            }

            var replacementFingerprint = request.NewSha256Fingerprint.Trim();
            if (EquivalentFingerprint(previous.Sha256Fingerprint, replacementFingerprint))
            {
                return MutationFailure(
                    ConnectionTrustIpcMessageTypes.RolloverResponse,
                    ConnectionTrustMutationStatus.ValidationFailed,
                    "connection.trust.rollover_same_identity",
                    StorageIpcFailureCategory.Validation,
                    "A rollover requires a different verified fingerprint.");
            }

            var revoked = previous with
            {
                Decision = TrustDecision.Revoked,
                LastSeenUtc = now,
                Version = checked(previous.Version + 1)
            };
            var replacement = new TrustRecord(
                TrustRecordId.New().Value.ToString("N"),
                target.ArtifactKind,
                target.CanonicalHost,
                target.Port,
                previous.Algorithm,
                replacementFingerprint,
                TrustDecision.Trusted,
                TrustDecisionSource.UserVerified,
                now,
                now,
                ExpiresUtc: null,
                PreviousFingerprint: previous.Sha256Fingerprint,
                Version: 1);
            await _trust.RolloverAsync(revoked, replacement, cancellationToken).ConfigureAwait(false);
            return await MutationSuccessAsync(
                ConnectionTrustIpcMessageTypes.RolloverResponse,
                resolved.Profile!,
                target,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TrustRecordConcurrencyException)
        {
            return MutationFailure(
                ConnectionTrustIpcMessageTypes.RolloverResponse,
                ConnectionTrustMutationStatus.VersionConflict,
                "connection.trust.version_conflict",
                StorageIpcFailureCategory.Conflict,
                "The trust record changed while rollover was being saved. Reload before retrying.");
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
        {
            return MutationFailure(
                ConnectionTrustIpcMessageTypes.RolloverResponse,
                ConnectionTrustMutationStatus.ValidationFailed,
                "connection.trust.validation_failed",
                StorageIpcFailureCategory.Validation,
                "The replacement server identity fingerprint is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return MutationFailure(
                ConnectionTrustIpcMessageTypes.RolloverResponse,
                ConnectionTrustMutationStatus.Unavailable,
                "connection.trust.unavailable",
                StorageIpcFailureCategory.Unavailable,
                "The trust rollover could not be saved.",
                isTransient: true);
        }
    }

    private async ValueTask<ResolvedTrustTarget> ResolveAsync(
        Guid connectionId,
        long expectedProfileVersion,
        CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(
            new ConnectionProfileId(connectionId),
            includeDeleted: false,
            cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return ResolvedTrustTarget.Failed(
                ConnectionTrustMutationStatus.NotFound,
                "connection.trust.profile_not_found",
                StorageIpcFailureCategory.NotFound,
                "The saved connection profile was not found.");
        }

        if (profile.Version != expectedProfileVersion)
        {
            return ResolvedTrustTarget.Failed(
                ConnectionTrustMutationStatus.VersionConflict,
                "connection.trust.profile_version_conflict",
                StorageIpcFailureCategory.Conflict,
                "The connection profile changed after it was loaded. Reload before changing trust.");
        }

        var target = profile.Endpoint switch
        {
            FtpsEndpoint { TlsPolicy: TlsCertificatePolicy.Pinned } endpoint => new TrustTarget(
                TrustArtifactKind.TlsCertificate, endpoint.Host, endpoint.Port),
            SftpEndpoint { HostKeyPolicy: SshHostKeyPolicy.Pinned } endpoint => new TrustTarget(
                TrustArtifactKind.SshHostKey, endpoint.Host, endpoint.Port),
            _ => null
        };
        return target is null
            ? ResolvedTrustTarget.Failed(
                ConnectionTrustMutationStatus.Unsupported,
                "connection.trust.profile_not_pinned",
                StorageIpcFailureCategory.Unsupported,
                "The selected profile does not use an enforceable pinned server identity policy.")
            : new ResolvedTrustTarget(profile, target, null);
    }

    private async ValueTask<ConnectionTrustSnapshot> SnapshotAsync(
        ConnectionProfile profile,
        TrustTarget target,
        CancellationToken cancellationToken)
    {
        var records = await _trust.FindAsync(
            target.ArtifactKind,
            target.CanonicalHost,
            target.Port,
            cancellationToken).ConfigureAwait(false);
        if (records.Count > ConnectionTrustIpcLimits.MaximumRecords)
        {
            throw new InvalidDataException("The endpoint has more trust records than the IPC contract permits.");
        }

        return new ConnectionTrustSnapshot(
            profile.Id.Value,
            profile.Version,
            new ConnectionTrustTargetDocument(
                MapArtifactKind(target.ArtifactKind),
                target.CanonicalHost,
                target.Port),
            records.Select(ToDocument).ToArray());
    }

    private async ValueTask<AgentIpcCommandResponse> MutationSuccessAsync(
        string responseType,
        ConnectionProfile profile,
        TrustTarget target,
        CancellationToken cancellationToken) => AgentIpcCommandResponse.Create(
            responseType,
            new ConnectionTrustMutationResponse(
                ConnectionTrustIpcContract.CurrentVersion,
                ConnectionTrustMutationStatus.Succeeded,
                await SnapshotAsync(profile, target, cancellationToken).ConfigureAwait(false)));

    private static TrustRecord? ResolveExisting(
        IReadOnlyList<TrustRecord> records,
        string? trustId,
        int? expectedVersion) => trustId is null
            ? null
            : records.SingleOrDefault(record =>
                string.Equals(record.TrustId, trustId, StringComparison.Ordinal) &&
                record.Version == expectedVersion);

    private static bool EquivalentFingerprint(string left, string right) =>
        TryDecodeFingerprint(left, out var leftBytes) &&
        TryDecodeFingerprint(right, out var rightBytes) &&
        leftBytes.AsSpan().SequenceEqual(rightBytes);

    private static bool TryDecodeFingerprint(string value, out byte[] bytes)
    {
        var normalized = value.Trim();
        var hexadecimal = normalized.Replace(":", string.Empty, StringComparison.Ordinal);
        try
        {
            bytes = hexadecimal.Length == 64 && hexadecimal.All(Uri.IsHexDigit)
                ? Convert.FromHexString(hexadecimal)
                : normalized.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(normalized[7..].PadRight((normalized.Length - 7 + 3) / 4 * 4, '='))
                    : [];
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static ConnectionTrustRecordDocument ToDocument(TrustRecord record) => new(
        record.TrustId,
        record.Sha256Fingerprint,
        record.Decision switch
        {
            TrustDecision.Trusted => ConnectionTrustDecision.Trusted,
            TrustDecision.Rejected => ConnectionTrustDecision.Rejected,
            TrustDecision.Revoked => ConnectionTrustDecision.Revoked,
            _ => throw new InvalidDataException("The stored trust decision is invalid.")
        },
        record.FirstSeenUtc,
        record.LastSeenUtc,
        record.ExpiresUtc,
        record.PreviousFingerprint,
        record.Version);

    private static TrustDecision MapDecision(ConnectionTrustDecision decision) => decision switch
    {
        ConnectionTrustDecision.Trusted => TrustDecision.Trusted,
        ConnectionTrustDecision.Rejected => TrustDecision.Rejected,
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    private static ConnectionTrustArtifactKind MapArtifactKind(TrustArtifactKind kind) => kind switch
    {
        TrustArtifactKind.TlsCertificate => ConnectionTrustArtifactKind.TlsCertificate,
        TrustArtifactKind.SshHostKey => ConnectionTrustArtifactKind.SshHostKey,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static AgentIpcCommandResponse GetFailure(
        string code,
        StorageIpcFailureCategory category,
        string message,
        bool isTransient = false) => AgentIpcCommandResponse.Create(
        ConnectionTrustIpcMessageTypes.GetResponse,
        new ConnectionTrustGetResponse(
            ConnectionTrustIpcContract.CurrentVersion,
            Snapshot: null,
            new StorageIpcFailure(code, category, message, isTransient)));

    private static AgentIpcCommandResponse MapResolvedFailure(
        string responseType,
        ResolvedFailure failure) => MutationFailure(
            responseType,
            failure.Status,
            failure.Code,
            failure.Category,
            failure.Message);

    private static AgentIpcCommandResponse MutationFailure(
        string responseType,
        ConnectionTrustMutationStatus status,
        string code,
        StorageIpcFailureCategory category,
        string message,
        bool isTransient = false) => AgentIpcCommandResponse.Create(
        responseType,
        new ConnectionTrustMutationResponse(
            ConnectionTrustIpcContract.CurrentVersion,
            status,
            Snapshot: null,
            new StorageIpcFailure(code, category, message, isTransient)));

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private sealed record TrustTarget(
        TrustArtifactKind ArtifactKind,
        string CanonicalHost,
        int Port);

    private sealed record ResolvedFailure(
        ConnectionTrustMutationStatus Status,
        string Code,
        StorageIpcFailureCategory Category,
        string Message);

    private sealed record ResolvedTrustTarget(
        ConnectionProfile? Profile,
        TrustTarget? Target,
        ResolvedFailure? Failure)
    {
        public static ResolvedTrustTarget Failed(
            ConnectionTrustMutationStatus status,
            string code,
            StorageIpcFailureCategory category,
            string message) => new(null, null, new ResolvedFailure(status, code, category, message));
    }
}
