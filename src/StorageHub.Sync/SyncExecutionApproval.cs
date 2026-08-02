using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Identifiers;
using StorageHub.Storage.Abstractions;
using StorageHub.Transfers;

namespace StorageHub.Sync;

/// <summary>
/// A canonical SHA-256 token binding approval to the immutable plan and every safety input that
/// can change its effect: scan completeness/counts, exact verified roots, live session roots,
/// deletion limits, execution mode, and overwrite behavior.
/// </summary>
public readonly record struct SyncExecutionApproval
{
    private SyncExecutionApproval(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("A SHA-256 digest must contain 32 bytes.", nameof(hash));
        }

        Sha256Hex = Convert.ToHexStringLower(hash);
    }

    public string? Sha256Hex { get; }

    public bool IsSpecified => Sha256Hex is not null;

    public static SyncExecutionApproval Create(
        ImmutableSyncPlan plan,
        IReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession> sessions,
        SyncExecutionSnapshots snapshots,
        SyncPlanExecutionMode mode = SyncPlanExecutionMode.Execute,
        DeletionSafetyPolicy? deletionPolicy = null,
        TransferExecutionOptions? transferOptions = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        deletionPolicy ??= DeletionSafetyPolicy.Default;
        transferOptions ??= new TransferExecutionOptions();
        return Compute(plan, sessions, snapshots, mode, deletionPolicy, transferOptions);
    }

    public static SyncExecutionApproval Parse(string sha256Hex)
    {
        if (!TryParse(sha256Hex, out var approval))
        {
            throw new FormatException("An execution approval must be exactly 64 hexadecimal characters.");
        }

        return approval;
    }

    public static bool TryParse(string? sha256Hex, out SyncExecutionApproval approval)
    {
        approval = default;
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

        approval = new SyncExecutionApproval(hash);
        return true;
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };

    internal static SyncExecutionApproval Compute(
        ImmutableSyncPlan plan,
        IReadOnlyDictionary<ConnectionProfileId, IStorageEndpointSession> sessions,
        SyncExecutionSnapshots snapshots,
        SyncPlanExecutionMode mode,
        DeletionSafetyPolicy deletionPolicy,
        TransferExecutionOptions transferOptions)
    {
        using var writer = new ApprovalDigestWriter();
        writer.AppendInt32(1); // Approval schema version.
        writer.AppendString(plan.Digest.Sha256Hex);
        writer.AppendInt32((int)mode);
        writer.AppendSnapshot(snapshots.Left);
        writer.AppendSnapshot(snapshots.Right);
        writer.AppendInt64(snapshots.BaselineItemCount);

        writer.AppendInt32(snapshots.VerifiedRootIdentities.Count);
        foreach (var (profileId, rootIdentity) in snapshots.VerifiedRootIdentities
                     .OrderBy(pair => pair.Key.Value))
        {
            writer.AppendGuid(profileId.Value);
            writer.AppendString(rootIdentity);
        }

        writer.AppendInt32(sessions.Count);
        foreach (var (profileId, session) in sessions.OrderBy(pair => pair.Key.Value))
        {
            writer.AppendGuid(profileId.Value);
            writer.AppendGuid(session.ProfileId.Value);
            writer.AppendString(session.RootIdentity);
            writer.AppendInt32((int)session.Capabilities.CaseSensitivity);
            foreach (var feature in Enum.GetValues<StorageFeature>())
            {
                writer.AppendInt32((int)feature);
                writer.AppendInt32((int)session.Capabilities[feature].Level);
            }
        }

        writer.AppendInt32(deletionPolicy.MaximumDeletionCount);
        foreach (var part in decimal.GetBits(deletionPolicy.MaximumDeletionPercentage))
        {
            writer.AppendInt32(part);
        }

        writer.AppendBoolean(transferOptions.Overwrite);
        writer.AppendInt32(transferOptions.BufferSize);
        return new SyncExecutionApproval(writer.GetHashAndReset());
    }

    public override string ToString() => Sha256Hex ?? string.Empty;

    private sealed class ApprovalDigestWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void AppendSnapshot(SnapshotCompleteness snapshot)
        {
            AppendBoolean(snapshot.EndpointAvailable);
            AppendBoolean(snapshot.RootIdentityVerified);
            AppendBoolean(snapshot.EnumerationCompleted);
            AppendBoolean(snapshot.PaginationCompleted);
            AppendBoolean(snapshot.PermissionsIntact);
            AppendBoolean(snapshot.UnexpectedlyEmpty);
            AppendInt64(snapshot.TotalItemCount);
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
