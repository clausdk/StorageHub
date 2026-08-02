namespace StorageHub.Security;

public enum TrustArtifactKind
{
    TlsCertificate,
    SshHostKey
}

public enum TrustDecision
{
    Trusted,
    Rejected,
    Revoked
}

public enum TrustDecisionSource
{
    UserVerified,
    AdministratorPolicy,
    ImportedPolicy
}

public sealed record TrustRecord(
    string TrustId,
    TrustArtifactKind ArtifactKind,
    string CanonicalHost,
    int Port,
    string Algorithm,
    string Sha256Fingerprint,
    TrustDecision Decision,
    TrustDecisionSource DecisionSource,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset? ExpiresUtc,
    string? PreviousFingerprint,
    int Version);

public interface ITrustStore
{
    ValueTask<IReadOnlyList<TrustRecord>> FindAsync(
        TrustArtifactKind artifactKind,
        string canonicalHost,
        int port,
        CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(TrustRecord record, CancellationToken cancellationToken = default);

    ValueTask<bool> RemoveAsync(
        string trustId,
        int expectedVersion,
        CancellationToken cancellationToken = default);
}

/// <summary>Authoritative mutation boundary for atomic server-identity rollover.</summary>
public interface ITrustManagementStore : ITrustStore
{
    ValueTask RolloverAsync(
        TrustRecord revokedRecord,
        TrustRecord replacementRecord,
        CancellationToken cancellationToken = default);
}
