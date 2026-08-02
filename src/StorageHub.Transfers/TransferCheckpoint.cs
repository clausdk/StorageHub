using System.Collections.Immutable;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;

namespace StorageHub.Transfers;

public sealed record TransferContentDigest
{
    public TransferContentDigest(string algorithm, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Algorithm = algorithm.ToUpperInvariant();
        Value = value;
    }

    public string Algorithm { get; }

    public string Value { get; }
}

public enum TransferResumeMode
{
    None = 0,
    Offset = 1,
    Multipart = 2,
}

public sealed record CompletedTransferPart
{
    public CompletedTransferPart(
        int partNumber,
        long offset,
        long length,
        string? providerTag)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ValidateOpaque(providerTag, nameof(providerTag));

        PartNumber = partNumber;
        Offset = offset;
        Length = length;
        ProviderTag = providerTag;
    }

    public int PartNumber { get; }

    public long Offset { get; }

    public long Length { get; }

    /// <summary>A non-secret provider checksum or part tag.</summary>
    public string? ProviderTag { get; }

    internal long EndOffset => checked(Offset + Length);

    private static void ValidateOpaque(string? value, string parameterName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > 8_192 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Provider tags must be bounded and cannot contain control characters.",
                parameterName);
        }
    }
}

/// <summary>
/// A durable recovery checkpoint. ProviderResumeId may identify server-side
/// state but must never contain a bearer credential or authorization token.
/// </summary>
public sealed record TransferCheckpoint
{
    private TransferCheckpoint(
        TransferJobId transferJobId,
        int attempt,
        long verifiedBytes,
        long? expectedLength,
        StorageAddress source,
        StorageAddress destinationTemporaryAddress,
        TransferResumeMode resumeMode,
        TransferContentDigest? sourceDigest,
        string? providerResumeId,
        ImmutableArray<CompletedTransferPart> completedParts,
        DateTimeOffset recordedAtUtc)
    {
        TransferJobId = transferJobId;
        Attempt = attempt;
        VerifiedBytes = verifiedBytes;
        ExpectedLength = expectedLength;
        Source = source;
        DestinationTemporaryAddress = destinationTemporaryAddress;
        ResumeMode = resumeMode;
        SourceDigest = sourceDigest;
        ProviderResumeId = providerResumeId;
        CompletedParts = completedParts;
        RecordedAtUtc = recordedAtUtc;
    }

    public TransferJobId TransferJobId { get; }

    public int Attempt { get; }

    public long VerifiedBytes { get; }

    public long? ExpectedLength { get; }

    public StorageAddress Source { get; }

    public StorageAddress DestinationTemporaryAddress { get; }

    public TransferResumeMode ResumeMode { get; }

    public TransferContentDigest? SourceDigest { get; }

    public string? ProviderResumeId { get; }

    public ImmutableArray<CompletedTransferPart> CompletedParts { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public static TransferCheckpoint Create(
        TransferJobId transferJobId,
        int attempt,
        long verifiedBytes,
        long? expectedLength,
        StorageAddress source,
        StorageAddress destinationTemporaryAddress,
        TransferResumeMode resumeMode,
        TransferContentDigest? sourceDigest,
        string? providerResumeId,
        IEnumerable<CompletedTransferPart> completedParts,
        DateTimeOffset recordedAtUtc)
    {
        if (transferJobId.IsEmpty)
        {
            throw new ArgumentException("A transfer job ID is required.", nameof(transferJobId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ArgumentOutOfRangeException.ThrowIfNegative(verifiedBytes);

        if (expectedLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }

        if (expectedLength is long length && verifiedBytes > length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verifiedBytes),
                "Verified bytes cannot exceed the expected source length.");
        }

        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationTemporaryAddress);
        ArgumentNullException.ThrowIfNull(completedParts);
        if (!Enum.IsDefined(resumeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(resumeMode));
        }

        ValidateResumeId(providerResumeId);

        if (recordedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Checkpoint time must be UTC.", nameof(recordedAtUtc));
        }

        var parts = completedParts
            .Select(part => part ?? throw new ArgumentException(
                "Completed parts cannot contain null entries.",
                nameof(completedParts)))
            .OrderBy(part => part.PartNumber)
            .ToImmutableArray();
        ValidateParts(parts, verifiedBytes, nameof(completedParts));
        ValidateResumeState(resumeMode, providerResumeId, parts);

        return new TransferCheckpoint(
            transferJobId,
            attempt,
            verifiedBytes,
            expectedLength,
            source,
            destinationTemporaryAddress,
            resumeMode,
            sourceDigest,
            providerResumeId,
            parts,
            recordedAtUtc);
    }

    public bool CanResumeFrom(
        StorageAddress currentSource,
        long currentLength,
        TransferContentDigest? currentDigest)
    {
        ArgumentNullException.ThrowIfNull(currentSource);
        ArgumentOutOfRangeException.ThrowIfNegative(currentLength);

        if (ResumeMode == TransferResumeMode.None)
        {
            return false;
        }

        if (ExpectedLength is not long expectedLength || currentLength != expectedLength)
        {
            return false;
        }

        if (!IsSameSource(Source, currentSource))
        {
            return false;
        }

        if (SourceDigest is not null && currentDigest is not null && SourceDigest != currentDigest)
        {
            return false;
        }

        if (Source.VersionId is not null)
        {
            return StringComparer.Ordinal.Equals(Source.VersionId, currentSource.VersionId);
        }

        if (Source.EntityTag is not null)
        {
            return StringComparer.Ordinal.Equals(Source.EntityTag, currentSource.EntityTag);
        }

        return SourceDigest is not null &&
               currentDigest is not null &&
               SourceDigest == currentDigest;
    }

    private static bool IsSameSource(StorageAddress expected, StorageAddress current) =>
        expected.ProfileId == current.ProfileId &&
        StringComparer.Ordinal.Equals(expected.RootIdentity, current.RootIdentity) &&
        StringComparer.Ordinal.Equals(expected.CanonicalRelativePath, current.CanonicalRelativePath) &&
        (expected.NativeItemId is null ||
         StringComparer.Ordinal.Equals(expected.NativeItemId, current.NativeItemId));

    private static void ValidateResumeId(string? providerResumeId)
    {
        if (providerResumeId is null)
        {
            return;
        }

        if (providerResumeId.Length is 0 or > 8_192 || providerResumeId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Provider resume IDs must be bounded and cannot contain control characters.",
                nameof(providerResumeId));
        }
    }

    private static void ValidateResumeState(
        TransferResumeMode resumeMode,
        string? providerResumeId,
        ImmutableArray<CompletedTransferPart> parts)
    {
        if (resumeMode == TransferResumeMode.None &&
            (providerResumeId is not null || !parts.IsEmpty))
        {
            throw new ArgumentException(
                "A non-resumable checkpoint cannot contain provider resume state.",
                nameof(resumeMode));
        }

        if (resumeMode == TransferResumeMode.Offset && !parts.IsEmpty)
        {
            throw new ArgumentException(
                "Offset resume cannot contain multipart checkpoints.",
                nameof(parts));
        }

        if (resumeMode == TransferResumeMode.Multipart && providerResumeId is null)
        {
            throw new ArgumentException(
                "Multipart resume requires a non-secret provider resume ID.",
                nameof(providerResumeId));
        }
    }

    private static void ValidateParts(
        ImmutableArray<CompletedTransferPart> parts,
        long verifiedBytes,
        string parameterName)
    {
        if (parts.Select(part => part.PartNumber).Distinct().Count() != parts.Length)
        {
            throw new ArgumentException("Completed part numbers must be unique.", parameterName);
        }

        var byOffset = parts.OrderBy(part => part.Offset).ToArray();
        long priorEnd = 0;
        foreach (var part in byOffset)
        {
            if (part.Offset < priorEnd)
            {
                throw new ArgumentException("Completed part ranges cannot overlap.", parameterName);
            }

            if (part.EndOffset > verifiedBytes)
            {
                throw new ArgumentException(
                    "Completed parts cannot extend beyond verified bytes.",
                    parameterName);
            }

            priorEnd = part.EndOffset;
        }
    }
}
