using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>Profile-bound management of authoritative TLS certificate and SSH host-key decisions.</summary>
public static class ConnectionTrustIpcContract
{
    public const int CurrentVersion = 1;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class ConnectionTrustIpcMessageTypes
{
    public const string GetRequest = "connection.trust.get.request";
    public const string GetResponse = "connection.trust.get.response";
    public const string DecideRequest = "connection.trust.decide.request";
    public const string DecideResponse = "connection.trust.decide.response";
    public const string RolloverRequest = "connection.trust.rollover.request";
    public const string RolloverResponse = "connection.trust.rollover.response";
}

public static class ConnectionTrustIpcLimits
{
    public const int MaximumHostLength = 253;
    public const int MaximumFingerprintLength = 128;
    public const int MaximumTrustIdLength = 128;
    public const int MaximumRecords = 64;

    public static bool IsValidFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumFingerprintLength ||
            value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var normalized = value.Trim();
        var hexadecimal = normalized.Replace(":", string.Empty, StringComparison.Ordinal);
        if (hexadecimal.Length == 64 && hexadecimal.All(Uri.IsHexDigit))
        {
            return true;
        }

        if (!normalized.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var payload = normalized[7..];
            return Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsValidTrustId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumTrustIdLength &&
        !value.Any(char.IsControl);
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionTrustArtifactKind>))]
public enum ConnectionTrustArtifactKind
{
    TlsCertificate = 1,
    SshHostKey = 2
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionTrustDecision>))]
public enum ConnectionTrustDecision
{
    Trusted = 1,
    Rejected = 2,
    Revoked = 3
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionTrustMutationStatus>))]
public enum ConnectionTrustMutationStatus
{
    Succeeded = 1,
    NotFound = 2,
    VersionConflict = 3,
    Unsupported = 4,
    ValidationFailed = 5,
    Unavailable = 6
}

public sealed record ConnectionTrustTargetDocument(
    ConnectionTrustArtifactKind ArtifactKind,
    string CanonicalHost,
    int Port)
{
    public bool HasValidBounds =>
        Enum.IsDefined(ArtifactKind) &&
        !string.IsNullOrWhiteSpace(CanonicalHost) &&
        CanonicalHost.Length <= ConnectionTrustIpcLimits.MaximumHostLength &&
        !CanonicalHost.Any(static character =>
            char.IsControl(character) || char.IsWhiteSpace(character) || character is '/' or '\\' or '@' or '?' or '#') &&
        Port is >= 1 and <= 65_535;
}

public sealed record ConnectionTrustRecordDocument(
    string TrustId,
    string Sha256Fingerprint,
    ConnectionTrustDecision Decision,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset? ExpiresUtc,
    string? PreviousFingerprint,
    int Version)
{
    public bool HasValidBounds =>
        ConnectionTrustIpcLimits.IsValidTrustId(TrustId) &&
        ConnectionTrustIpcLimits.IsValidFingerprint(Sha256Fingerprint) &&
        Enum.IsDefined(Decision) &&
        FirstSeenUtc.Offset == TimeSpan.Zero &&
        LastSeenUtc.Offset == TimeSpan.Zero &&
        LastSeenUtc >= FirstSeenUtc &&
        (ExpiresUtc is null || ExpiresUtc.Value.Offset == TimeSpan.Zero && ExpiresUtc >= FirstSeenUtc) &&
        (PreviousFingerprint is null || ConnectionTrustIpcLimits.IsValidFingerprint(PreviousFingerprint)) &&
        Version > 0;
}

public sealed record ConnectionTrustSnapshot(
    Guid ConnectionId,
    long ProfileVersion,
    ConnectionTrustTargetDocument Target,
    ConnectionTrustRecordDocument[] Records)
{
    public bool HasValidBounds =>
        ConnectionId != Guid.Empty &&
        ProfileVersion > 0 &&
        Target is { HasValidBounds: true } &&
        Records is { Length: <= ConnectionTrustIpcLimits.MaximumRecords } &&
        Records.All(static record => record is { HasValidBounds: true }) &&
        Records.Select(static record => record.TrustId).Distinct(StringComparer.Ordinal).Count() == Records.Length;
}

public sealed record ConnectionTrustGetRequest(
    int ContractVersion,
    Guid ConnectionId,
    long ExpectedProfileVersion)
{
    public bool HasValidBounds =>
        ContractVersion > 0 && ConnectionId != Guid.Empty && ExpectedProfileVersion > 0;
}

public sealed record ConnectionTrustGetResponse(
    int ContractVersion,
    ConnectionTrustSnapshot? Snapshot,
    StorageIpcFailure? Failure = null);

public sealed record ConnectionTrustDecisionRequest(
    int ContractVersion,
    Guid ConnectionId,
    long ExpectedProfileVersion,
    string Sha256Fingerprint,
    ConnectionTrustDecision Decision,
    string? ExistingTrustId = null,
    int? ExpectedTrustVersion = null)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        ConnectionId != Guid.Empty &&
        ExpectedProfileVersion > 0 &&
        ConnectionTrustIpcLimits.IsValidFingerprint(Sha256Fingerprint) &&
        Decision is ConnectionTrustDecision.Trusted or ConnectionTrustDecision.Rejected &&
        (ExistingTrustId is null && ExpectedTrustVersion is null ||
         ConnectionTrustIpcLimits.IsValidTrustId(ExistingTrustId) && ExpectedTrustVersion > 0);
}

public sealed record ConnectionTrustRolloverRequest(
    int ContractVersion,
    Guid ConnectionId,
    long ExpectedProfileVersion,
    string PreviousTrustId,
    int ExpectedPreviousTrustVersion,
    string NewSha256Fingerprint)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        ConnectionId != Guid.Empty &&
        ExpectedProfileVersion > 0 &&
        ConnectionTrustIpcLimits.IsValidTrustId(PreviousTrustId) &&
        ExpectedPreviousTrustVersion > 0 &&
        ConnectionTrustIpcLimits.IsValidFingerprint(NewSha256Fingerprint);
}

public sealed record ConnectionTrustMutationResponse(
    int ContractVersion,
    ConnectionTrustMutationStatus Status,
    ConnectionTrustSnapshot? Snapshot = null,
    StorageIpcFailure? Failure = null);
