namespace StorageHub.Sync;

public sealed record ContentDigest
{
    public ContentDigest(string algorithm, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Algorithm = algorithm.ToUpperInvariant();
        Value = value;
    }

    public string Algorithm { get; }

    public string Value { get; }
}

public sealed record SyncItemObservation
{
    private SyncItemObservation(
        bool exists,
        long length,
        ContentDigest? digest,
        string? versionId)
    {
        Exists = exists;
        Length = length;
        Digest = digest;
        VersionId = versionId;
    }

    public static SyncItemObservation Missing { get; } = new(false, 0, null, null);

    public bool Exists { get; }

    public long Length { get; }

    public ContentDigest? Digest { get; }

    public string? VersionId { get; }

    public static SyncItemObservation Present(
        long length,
        ContentDigest? digest,
        string? versionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new SyncItemObservation(true, length, digest, Normalize(versionId));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed record SyncBaselineObservation
{
    private SyncBaselineObservation(
        bool exists,
        long length,
        ContentDigest? digest,
        string? leftVersionId,
        string? rightVersionId)
    {
        Exists = exists;
        Length = length;
        Digest = digest;
        LeftVersionId = leftVersionId;
        RightVersionId = rightVersionId;
    }

    public static SyncBaselineObservation Missing { get; } =
        new(false, 0, null, null, null);

    public bool Exists { get; }

    public long Length { get; }

    public ContentDigest? Digest { get; }

    public string? LeftVersionId { get; }

    public string? RightVersionId { get; }

    public static SyncBaselineObservation Present(
        long length,
        ContentDigest? digest,
        string? leftVersionId,
        string? rightVersionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return new SyncBaselineObservation(
            true,
            length,
            digest,
            Normalize(leftVersionId),
            Normalize(rightVersionId));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
