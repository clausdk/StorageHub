using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Transfers;

namespace StorageHub.Sync;

public enum SyncConflictPolicy
{
    Block = 0,
}

/// <summary>
/// Immutable, provider-neutral sync policy. The policy hash binds every setting that can alter
/// the meaning or safety of a generated plan. Display name, enabled state, and persistence
/// timestamps deliberately do not affect it.
/// </summary>
public sealed class SyncProfile
{
    public SyncProfile(
        SyncProfileId profileId,
        string displayName,
        ConnectionProfileId leftConnectionProfileId,
        string leftRoot,
        ConnectionProfileId rightConnectionProfileId,
        string rightRoot,
        SyncDirection direction,
        SyncDeletionMode deletionMode,
        SyncConflictPolicy conflictPolicy,
        DeletionSafetyPolicy deletionSafetyPolicy,
        TransferExecutionOptions transferOptions,
        bool enabled,
        long revision,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (profileId.IsEmpty)
        {
            throw new ArgumentException("A sync profile ID is required.", nameof(profileId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (displayName.Trim().Length > 256)
        {
            throw new ArgumentException("A sync profile display name cannot exceed 256 characters.", nameof(displayName));
        }

        if (leftConnectionProfileId.IsEmpty || rightConnectionProfileId.IsEmpty)
        {
            throw new ArgumentException("Both connection profile IDs are required.");
        }

        if (leftConnectionProfileId == rightConnectionProfileId)
        {
            throw new ArgumentException(
                "The current execution contract requires distinct left and right connection profiles.");
        }

        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(deletionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(deletionMode));
        }

        if (!Enum.IsDefined(conflictPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
        }

        if (direction == SyncDirection.TwoWay && deletionMode == SyncDeletionMode.Mirror ||
            direction != SyncDirection.TwoWay && deletionMode == SyncDeletionMode.Propagate)
        {
            throw new ArgumentException("The deletion mode is not valid for the selected sync direction.");
        }

        ArgumentNullException.ThrowIfNull(deletionSafetyPolicy);
        ArgumentNullException.ThrowIfNull(transferOptions);
        if (transferOptions.BufferSize is <= 0 or > BoundedStreamCopier.MaximumBufferSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transferOptions),
                $"The transfer buffer must be between 1 and {BoundedStreamCopier.MaximumBufferSize} bytes.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        RequireUtc(createdAtUtc, nameof(createdAtUtc));
        RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(
            updatedAtUtc,
            createdAtUtc,
            nameof(updatedAtUtc));

        ProfileId = profileId;
        DisplayName = displayName.Trim();
        LeftConnectionProfileId = leftConnectionProfileId;
        LeftRoot = ValidateCanonicalRoot(leftRoot, nameof(leftRoot));
        RightConnectionProfileId = rightConnectionProfileId;
        RightRoot = ValidateCanonicalRoot(rightRoot, nameof(rightRoot));
        Direction = direction;
        DeletionMode = deletionMode;
        ConflictPolicy = conflictPolicy;
        DeletionSafetyPolicy = deletionSafetyPolicy;
        TransferOptions = transferOptions;
        Enabled = enabled;
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        PolicySha256 = ComputePolicySha256(this);
    }

    public SyncProfileId ProfileId { get; }
    public string DisplayName { get; }
    public ConnectionProfileId LeftConnectionProfileId { get; }
    public string LeftRoot { get; }
    public ConnectionProfileId RightConnectionProfileId { get; }
    public string RightRoot { get; }
    public SyncDirection Direction { get; }
    public SyncDeletionMode DeletionMode { get; }
    public SyncConflictPolicy ConflictPolicy { get; }
    public DeletionSafetyPolicy DeletionSafetyPolicy { get; }
    public TransferExecutionOptions TransferOptions { get; }
    public bool Enabled { get; }
    public long Revision { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public string PolicySha256 { get; }

    public SyncProfile WithPersistence(long revision, DateTimeOffset updatedAtUtc) => new(
        ProfileId,
        DisplayName,
        LeftConnectionProfileId,
        LeftRoot,
        RightConnectionProfileId,
        RightRoot,
        Direction,
        DeletionMode,
        ConflictPolicy,
        DeletionSafetyPolicy,
        TransferOptions,
        Enabled,
        revision,
        CreatedAtUtc,
        updatedAtUtc);

    private static string ValidateCanonicalRoot(string? root, string parameterName)
    {
        if (root is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        var validation = StorageAddress.Create(
            new ConnectionProfileId(new Guid("9f456db7-5ef4-42d9-b852-b3933da98295")),
            "sync-profile-root-validation",
            root);
        if (validation.IsFailure ||
            !StringComparer.Ordinal.Equals(validation.Value.CanonicalRelativePath, root))
        {
            throw new ArgumentException("Sync roots must be canonical root-relative paths.", parameterName);
        }

        return root;
    }

    private static string ComputePolicySha256(SyncProfile profile)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, 1);
        AppendGuid(hash, profile.LeftConnectionProfileId.Value);
        AppendString(hash, profile.LeftRoot);
        AppendGuid(hash, profile.RightConnectionProfileId.Value);
        AppendString(hash, profile.RightRoot);
        AppendInt32(hash, (int)profile.Direction);
        AppendInt32(hash, (int)profile.DeletionMode);
        AppendInt32(hash, (int)profile.ConflictPolicy);
        AppendInt32(hash, profile.DeletionSafetyPolicy.MaximumDeletionCount);
        foreach (var value in decimal.GetBits(profile.DeletionSafetyPolicy.MaximumDeletionPercentage))
        {
            AppendInt32(hash, value);
        }

        AppendInt32(hash, profile.TransferOptions.Overwrite ? 1 : 0);
        AppendInt32(hash, profile.TransferOptions.BufferSize);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendGuid(IncrementalHash hash, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = value.TryWriteBytes(bytes, bigEndian: true, out _);
        hash.AppendData(bytes);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Sync profile timestamps must be UTC.", parameterName);
        }
    }
}
