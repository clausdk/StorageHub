using StorageHub.Contracts.Results;
using StorageHub.Domain.Storage;

namespace StorageHub.Storage.Abstractions;

public static class StorageEndpointContractExtensions
{
    /// <summary>Rejects addresses created for another profile or a previous root configuration.</summary>
    public static StorageResult ValidateAddress(
        this IStorageEndpointSession session,
        StorageAddress address)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(address);
        if (session.ProfileId != address.ProfileId)
        {
            return StorageResult.Fail(new StorageFailure(
                "storage.address.profile_mismatch",
                StorageFailureKind.Validation,
                "The storage address belongs to a different connection profile."));
        }

        if (!string.Equals(session.RootIdentity, address.RootIdentity, StringComparison.Ordinal))
        {
            return StorageResult.Fail(new StorageFailure(
                "storage.address.root_mismatch",
                StorageFailureKind.Conflict,
                "The connection root changed after this storage address was created."));
        }

        return StorageResult.Success();
    }
}
