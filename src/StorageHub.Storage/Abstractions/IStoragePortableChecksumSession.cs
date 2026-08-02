using StorageHub.Contracts.Results;
using StorageHub.Domain.Storage;

namespace StorageHub.Storage.Abstractions;

/// <summary>Algorithms whose output has a portable, provider-independent meaning.</summary>
public enum PortableChecksumAlgorithm
{
    Sha256 = 0,
}

/// <summary>
/// A validated portable digest. Provider ETags and opaque checksum fields must never be converted
/// to this type unless their algorithm and full value were explicitly proven.
/// </summary>
public sealed record PortableContentDigest
{
    public PortableContentDigest(PortableChecksumAlgorithm algorithm, string value)
    {
        if (!Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (algorithm == PortableChecksumAlgorithm.Sha256 && !IsHex(value, 64))
        {
            throw new ArgumentException(
                "A portable SHA-256 digest must contain exactly 64 hexadecimal characters.",
                nameof(value));
        }

        Algorithm = algorithm;
        Value = value.ToLowerInvariant();
    }

    public PortableChecksumAlgorithm Algorithm { get; }

    public string Value { get; }

    public string AlgorithmName => Algorithm switch
    {
        PortableChecksumAlgorithm.Sha256 => "SHA256",
        _ => throw new InvalidOperationException("The portable checksum algorithm is unsupported."),
    };

    public static PortableContentDigest Parse(string algorithm, string value)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(algorithm, "SHA256"))
        {
            throw new FormatException("Only portable SHA-256 evidence is supported.");
        }

        try
        {
            return new PortableContentDigest(PortableChecksumAlgorithm.Sha256, value);
        }
        catch (ArgumentException error)
        {
            throw new FormatException("The portable SHA-256 evidence is invalid.", error);
        }
    }

    private static bool IsHex(string value, int requiredLength) =>
        value.Length == requiredLength && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

/// <summary>
/// A bounded request to checksum the exact file observation captured by a scan or transfer.
/// Implementations must stream; MaximumBytes is a hard limit, not a buffer allocation request.
/// </summary>
public sealed record PortableChecksumRequest
{
    public PortableChecksumRequest(
        StorageEntry expectedEntry,
        long maximumBytes,
        PortableChecksumAlgorithm algorithm = PortableChecksumAlgorithm.Sha256)
    {
        ExpectedEntry = expectedEntry ?? throw new ArgumentNullException(nameof(expectedEntry));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (!Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm));
        }

        MaximumBytes = maximumBytes;
        Algorithm = algorithm;
    }

    public StorageEntry ExpectedEntry { get; }

    public long MaximumBytes { get; }

    public PortableChecksumAlgorithm Algorithm { get; }

    public StorageResult Validate()
    {
        if (ExpectedEntry.Kind != StorageEntryKind.File || ExpectedEntry.Size is not long length)
        {
            return Invalid("Portable checksums require a file with a known length.");
        }

        if (length > MaximumBytes)
        {
            return Invalid("The file exceeds the configured portable-checksum byte limit.");
        }

        return StorageResult.Success();
    }

    private static StorageResult Invalid(string message) => StorageResult.Fail(new StorageFailure(
        "storage.checksum.invalid_request",
        StorageFailureKind.Validation,
        message));
}

public sealed record PortableChecksumResult
{
    public PortableChecksumResult(PortableContentDigest digest, long bytesProcessed)
    {
        Digest = digest ?? throw new ArgumentNullException(nameof(digest));
        ArgumentOutOfRangeException.ThrowIfNegative(bytesProcessed);
        BytesProcessed = bytesProcessed;
    }

    public PortableContentDigest Digest { get; }

    public long BytesProcessed { get; }
}

/// <summary>
/// Optional endpoint capability for explicit, streamed portable checksums. Its presence is the
/// capability check; generic provider checksum metadata is deliberately insufficient.
/// </summary>
public interface IStoragePortableChecksumSession
{
    ValueTask<StorageResult<PortableChecksumResult>> ComputePortableChecksumAsync(
        PortableChecksumRequest request,
        CancellationToken cancellationToken = default);
}
