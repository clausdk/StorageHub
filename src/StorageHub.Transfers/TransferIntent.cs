using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;

namespace StorageHub.Transfers;

public enum TransferOperationKind
{
    Copy = 0,
    Move = 1,
}

public enum TransferVerificationPolicy
{
    Size = 0,
    StrongHashWhenAvailable = 1,
    StrongHashRequired = 2,
}

/// <summary>
/// Immutable transfer intent persisted before any provider side effect.
/// Addresses carry the canonical path, root identity, and observed version.
/// </summary>
public sealed record TransferIntent
{
    public TransferIntent(
        TransferJobId transferJobId,
        TransferOperationKind operation,
        StorageAddress source,
        StorageAddress destination,
        long? expectedLength,
        TransferVerificationPolicy verificationPolicy,
        DateTimeOffset createdAtUtc,
        string? expectedDestinationVersionId = null,
        string? expectedDestinationEntityTag = null,
        PortableContentDigest? expectedSourceDigest = null,
        PortableContentDigest? expectedDestinationDigest = null,
        PortableContentDigest? requiredDestinationDigest = null)
    {
        if (transferJobId.IsEmpty)
        {
            throw new ArgumentException("A transfer job ID is required.", nameof(transferJobId));
        }

        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (!Enum.IsDefined(verificationPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(verificationPolicy));
        }

        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (IsSameLogicalAddress(source, destination))
        {
            throw new ArgumentException(
                "Source and destination must identify different storage items.",
                nameof(destination));
        }

        if (expectedLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Creation time must be UTC.", nameof(createdAtUtc));
        }

        var destinationVersion = expectedDestinationVersionId ?? destination.VersionId;
        var destinationEntityTag = expectedDestinationEntityTag ?? destination.EntityTag;
        if (!IsValidIdentityToken(destinationVersion))
        {
            throw new ArgumentException(
                "The expected destination version ID cannot be blank or contain control characters.",
                nameof(expectedDestinationVersionId));
        }

        if (!IsValidIdentityToken(destinationEntityTag))
        {
            throw new ArgumentException(
                "The expected destination entity tag cannot be blank or contain control characters.",
                nameof(expectedDestinationEntityTag));
        }

        if (expectedSourceDigest is not null &&
            requiredDestinationDigest is not null &&
            expectedSourceDigest != requiredDestinationDigest)
        {
            throw new ArgumentException(
                "A byte-for-byte transfer cannot require a destination digest different from its planned source digest.",
                nameof(requiredDestinationDigest));
        }

        TransferJobId = transferJobId;
        Operation = operation;
        Source = source;
        Destination = destination;
        ExpectedLength = expectedLength;
        VerificationPolicy = verificationPolicy;
        CreatedAtUtc = createdAtUtc;
        ExpectedDestinationVersionId = destinationVersion;
        ExpectedDestinationEntityTag = destinationEntityTag;
        ExpectedSourceDigest = expectedSourceDigest;
        ExpectedDestinationDigest = expectedDestinationDigest;
        RequiredDestinationDigest = requiredDestinationDigest;
    }

    public TransferJobId TransferJobId { get; }

    public TransferOperationKind Operation { get; }

    public StorageAddress Source { get; }

    public StorageAddress Destination { get; }

    public long? ExpectedLength { get; }

    public TransferVerificationPolicy VerificationPolicy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// The immutable destination identity observed while planning. Destructive overwrites must
    /// carry this value into the provider's conditional promote/write operation.
    /// </summary>
    public string? ExpectedDestinationVersionId { get; }

    /// <summary>
    /// The immutable destination entity tag observed while planning. Conditional providers must
    /// compare it atomically when no version/generation identifier is available.
    /// </summary>
    public string? ExpectedDestinationEntityTag { get; }

    /// <summary>Portable source bytes approved by planning and verified before publication.</summary>
    public PortableContentDigest? ExpectedSourceDigest { get; }

    /// <summary>Portable destination bytes observed before an approved overwrite.</summary>
    public PortableContentDigest? ExpectedDestinationDigest { get; }

    /// <summary>Portable final destination bytes that must be verified after publication.</summary>
    public PortableContentDigest? RequiredDestinationDigest { get; }

    private static bool IsValidIdentityToken(string? value) =>
        value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 8_192 &&
        !value.Any(char.IsControl);

    private static bool IsSameLogicalAddress(StorageAddress left, StorageAddress right) =>
        left.ProfileId == right.ProfileId &&
        StringComparer.Ordinal.Equals(left.RootIdentity, right.RootIdentity) &&
        StringComparer.Ordinal.Equals(left.CanonicalRelativePath, right.CanonicalRelativePath);
}
