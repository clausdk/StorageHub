using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;

namespace StorageHub.Sync;

public enum SyncPlanOperationKind
{
    Copy = 0,
    Delete = 1,
    CreateDirectory = 2,
}

public sealed record SyncPlanOperation
{
    private SyncPlanOperation(
        int sequence,
        SyncPlanOperationKind kind,
        StorageAddress sourceOrTarget,
        StorageAddress? destination,
        long? expectedLength,
        PortableContentDigest? sourceDigest,
        PortableContentDigest? destinationDigest,
        bool destinationExisted)
    {
        Sequence = sequence;
        Kind = kind;
        SourceOrTarget = sourceOrTarget;
        Destination = destination;
        ExpectedLength = expectedLength;
        SourceDigest = sourceDigest;
        DestinationDigest = destinationDigest;
        DestinationExisted = destinationExisted;
    }

    public int Sequence { get; }

    public SyncPlanOperationKind Kind { get; }

    /// <summary>The source for copy, or the target for delete/create-directory.</summary>
    public StorageAddress SourceOrTarget { get; }

    public StorageAddress? Destination { get; }

    public long? ExpectedLength { get; }

    /// <summary>Portable source content evidence captured by the planning snapshot.</summary>
    public PortableContentDigest? SourceDigest { get; }

    /// <summary>Portable pre-operation destination evidence captured by the planning snapshot.</summary>
    public PortableContentDigest? DestinationDigest { get; }

    /// <summary>Whether the destination was present in the complete planning snapshot.</summary>
    public bool DestinationExisted { get; }

    public bool IsDestructive => Kind == SyncPlanOperationKind.Delete;

    public static SyncPlanOperation Copy(
        int sequence,
        StorageAddress source,
        StorageAddress destination,
        long? expectedLength,
        PortableContentDigest? sourceDigest = null,
        PortableContentDigest? destinationDigest = null,
        bool destinationExisted = false)
    {
        ValidateSequence(sequence);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (expectedLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        }

        return new SyncPlanOperation(
            sequence,
            SyncPlanOperationKind.Copy,
            source,
            destination,
            expectedLength,
            sourceDigest,
            destinationDigest,
            destinationExisted);
    }

    public static SyncPlanOperation Delete(int sequence, StorageAddress target)
    {
        ValidateSequence(sequence);
        ArgumentNullException.ThrowIfNull(target);
        return new SyncPlanOperation(sequence, SyncPlanOperationKind.Delete, target, null, null, null, null, false);
    }

    public static SyncPlanOperation CreateDirectory(int sequence, StorageAddress target)
    {
        ValidateSequence(sequence);
        ArgumentNullException.ThrowIfNull(target);
        return new SyncPlanOperation(
            sequence,
            SyncPlanOperationKind.CreateDirectory,
            target,
            null,
            null,
            null,
            null,
            false);
    }

    private static void ValidateSequence(int sequence) =>
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
}

public readonly record struct SyncPlanDigest
{
    internal SyncPlanDigest(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("A SHA-256 digest must contain 32 bytes.", nameof(hash));
        }

        Sha256Hex = Convert.ToHexStringLower(hash);
    }

    public string Sha256Hex { get; }

    public static SyncPlanDigest Parse(string sha256Hex)
    {
        if (!TryParse(sha256Hex, out var digest))
        {
            throw new FormatException("A sync plan digest must be exactly 64 hexadecimal characters.");
        }

        return digest;
    }

    public static bool TryParse(string? sha256Hex, out SyncPlanDigest digest)
    {
        digest = default;
        if (sha256Hex is null || sha256Hex.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        for (var index = 0; index < hash.Length; index++)
        {
            var high = HexValue(sha256Hex[index * 2]);
            var low = HexValue(sha256Hex[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            hash[index] = (byte)((high << 4) | low);
        }

        digest = new SyncPlanDigest(hash);
        return true;
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };

    public override string ToString() => Sha256Hex;
}

/// <summary>
/// Immutable, canonically ordered plan. Its digest binds approval to the
/// profile, baseline generation, creation instant, and every operation
/// precondition carried by each version-aware storage address.
/// </summary>
public sealed record ImmutableSyncPlan
{
    public const int CurrentDigestSchemaVersion = 4;

    private ImmutableSyncPlan(
        OperationPlanId planId,
        SyncProfileId profileId,
        long baselineGeneration,
        ImmutableArray<SyncPlanOperation> operations,
        DateTimeOffset createdAtUtc,
        SyncPlanDigest digest,
        int digestSchemaVersion)
    {
        PlanId = planId;
        ProfileId = profileId;
        BaselineGeneration = baselineGeneration;
        Operations = operations;
        CreatedAtUtc = createdAtUtc;
        Digest = digest;
        DigestSchemaVersion = digestSchemaVersion;
    }

    public OperationPlanId PlanId { get; }

    public SyncProfileId ProfileId { get; }

    public long BaselineGeneration { get; }

    public ImmutableArray<SyncPlanOperation> Operations { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public SyncPlanDigest Digest { get; }

    /// <summary>The canonical digest schema used by this plan; persisted v2 plans remain readable.</summary>
    public int DigestSchemaVersion { get; }

    /// <summary>
    /// Recomputes the canonical digest from the immutable plan payload. Execution code uses this
    /// in addition to checking the separately approved digest, so corrupted persisted plans fail
    /// closed before provider I/O.
    /// </summary>
    public bool HasValidDigest => Digest == ComputeDigest(
        PlanId,
        ProfileId,
        BaselineGeneration,
        Operations,
        CreatedAtUtc,
        DigestSchemaVersion);

    public static ImmutableSyncPlan Create(
        OperationPlanId planId,
        SyncProfileId profileId,
        long baselineGeneration,
        IEnumerable<SyncPlanOperation> operations,
        DateTimeOffset createdAtUtc) => CreateCore(
        planId,
        profileId,
        baselineGeneration,
        operations,
        createdAtUtc,
        CurrentDigestSchemaVersion);

    /// <summary>Restores a persisted plan using the digest schema stored alongside it.</summary>
    public static ImmutableSyncPlan Restore(
        OperationPlanId planId,
        SyncProfileId profileId,
        long baselineGeneration,
        IEnumerable<SyncPlanOperation> operations,
        DateTimeOffset createdAtUtc,
        int digestSchemaVersion) => CreateCore(
        planId,
        profileId,
        baselineGeneration,
        operations,
        createdAtUtc,
        digestSchemaVersion);

    private static ImmutableSyncPlan CreateCore(
        OperationPlanId planId,
        SyncProfileId profileId,
        long baselineGeneration,
        IEnumerable<SyncPlanOperation> operations,
        DateTimeOffset createdAtUtc,
        int digestSchemaVersion)
    {
        if (planId.IsEmpty)
        {
            throw new ArgumentException("A plan ID is required.", nameof(planId));
        }

        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(baselineGeneration);
        ArgumentNullException.ThrowIfNull(operations);

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Plan creation time must be UTC.", nameof(createdAtUtc));
        }

        if (digestSchemaVersion is not (2 or 3 or CurrentDigestSchemaVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(digestSchemaVersion),
                "Only sync plan digest schemas 2, 3, and 4 are supported.");
        }

        var immutableOperations = operations
            .Select(operation => operation ?? throw new ArgumentException(
                "Plan operations cannot contain null entries.",
                nameof(operations)))
            .OrderBy(operation => operation.Sequence)
            .ToImmutableArray();
        ValidateSequences(immutableOperations, nameof(operations));
        if (digestSchemaVersion < CurrentDigestSchemaVersion &&
            immutableOperations.Any(operation =>
                digestSchemaVersion < 3 &&
                    (operation.SourceDigest is not null || operation.DestinationDigest is not null) ||
                digestSchemaVersion < 4 && operation.DestinationExisted))
        {
            throw new ArgumentException(
                "Legacy digest schemas cannot carry portable content evidence.",
                nameof(operations));
        }

        var digest = ComputeDigest(
            planId,
            profileId,
            baselineGeneration,
            immutableOperations,
            createdAtUtc,
            digestSchemaVersion);

        return new ImmutableSyncPlan(
            planId,
            profileId,
            baselineGeneration,
            immutableOperations,
            createdAtUtc,
            digest,
            digestSchemaVersion);
    }

    private static void ValidateSequences(
        ImmutableArray<SyncPlanOperation> operations,
        string parameterName)
    {
        for (var index = 0; index < operations.Length; index++)
        {
            if (operations[index].Sequence != index)
            {
                throw new ArgumentException(
                    "Plan operation sequences must be unique, contiguous, and start at zero.",
                    parameterName);
            }
        }
    }

    private static SyncPlanDigest ComputeDigest(
        OperationPlanId planId,
        SyncProfileId profileId,
        long baselineGeneration,
        ImmutableArray<SyncPlanOperation> operations,
        DateTimeOffset createdAtUtc,
        int digestSchemaVersion)
    {
        using var writer = new CanonicalDigestWriter();
        writer.AppendInt32(digestSchemaVersion);
        writer.AppendGuid(planId.Value);
        writer.AppendGuid(profileId.Value);
        writer.AppendInt64(baselineGeneration);
        writer.AppendInt64(createdAtUtc.Ticks);
        writer.AppendInt32(operations.Length);

        foreach (var operation in operations)
        {
            writer.AppendInt32(operation.Sequence);
            writer.AppendInt32((int)operation.Kind);
            writer.AppendAddress(operation.SourceOrTarget);
            writer.AppendNullableAddress(operation.Destination);
            writer.AppendNullableInt64(operation.ExpectedLength);
            if (digestSchemaVersion >= 3)
            {
                writer.AppendNullableDigest(operation.SourceDigest);
                writer.AppendNullableDigest(operation.DestinationDigest);
            }
            if (digestSchemaVersion >= 4)
            {
                writer.AppendBoolean(operation.DestinationExisted);
            }
        }

        return new SyncPlanDigest(writer.GetHashAndReset());
    }

    private sealed class CanonicalDigestWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void AppendAddress(StorageAddress address)
        {
            AppendGuid(address.ProfileId.Value);
            AppendString(address.RootIdentity);
            AppendString(address.CanonicalRelativePath);
            AppendNullableString(address.NativeItemId);
            AppendNullableString(address.VersionId);
            AppendNullableString(address.EntityTag);
        }

        public void AppendNullableAddress(StorageAddress? address)
        {
            AppendBoolean(address is not null);
            if (address is not null)
            {
                AppendAddress(address);
            }
        }

        public void AppendNullableInt64(long? value)
        {
            AppendBoolean(value.HasValue);
            if (value.HasValue)
            {
                AppendInt64(value.Value);
            }
        }

        public void AppendNullableString(string? value)
        {
            AppendBoolean(value is not null);
            if (value is not null)
            {
                AppendString(value);
            }
        }

        public void AppendNullableDigest(PortableContentDigest? digest)
        {
            AppendBoolean(digest is not null);
            if (digest is not null)
            {
                AppendInt32((int)digest.Algorithm);
                AppendString(digest.Value);
            }
        }

        public void AppendString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            AppendInt32(bytes.Length);
            _hash.AppendData(bytes);
        }

        public void AppendGuid(Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            _ = value.TryWriteBytes(bytes, bigEndian: true, out _);
            _hash.AppendData(bytes);
        }

        public void AppendBoolean(bool value)
        {
            Span<byte> bytes = stackalloc byte[1];
            bytes[0] = value ? (byte)1 : (byte)0;
            _hash.AppendData(bytes);
        }

        public void AppendInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void AppendInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public void Dispose() => _hash.Dispose();
    }
}
