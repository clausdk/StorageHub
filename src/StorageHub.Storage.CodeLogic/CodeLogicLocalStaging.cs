using CL.Storage.Abstractions;
using CL.Storage.Models;

namespace StorageHub.Storage.CodeLogic;

/// <summary>Owns the reserved local-provider namespace used for atomic publication.</summary>
internal static class CodeLogicLocalStaging
{
    internal const string ReservedRootName = ".storagehub-internal";
    internal const string StagingRootPath = ReservedRootName + "/staging";
    private const string StagingPrefix = StagingRootPath + "/";
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);

    internal static string CreatePath() =>
        $"{StagingPrefix}{System.Security.Cryptography.RandomNumberGenerator.GetHexString(64, lowercase: true)}.partial";

    internal static bool IsReserved(string canonicalRelativePath) =>
        canonicalRelativePath.Equals(ReservedRootName, StringComparison.OrdinalIgnoreCase) ||
        canonicalRelativePath.StartsWith(ReservedRootName + "/", StringComparison.OrdinalIgnoreCase);

    internal static async ValueTask ScavengeOrphansAsync(
        IStorageService storage,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.Provider != StorageProvider.Local)
        {
            return;
        }

        var cutoffUtc = (timeProvider ?? TimeProvider.System).GetUtcNow() - OrphanAge;
        string? continuationToken = null;
        var observedTokens = new HashSet<string>(StringComparer.Ordinal);
        for (var pageIndex = 0; pageIndex < 10_000; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await storage.ListAsync(
                    StagingRootPath,
                    new StorageListOptions
                    {
                        Recursive = false,
                        PageSize = 1_000,
                        ContinuationToken = continuationToken
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (page.IsFailure)
            {
                return;
            }

            foreach (var item in page.Value!.Items)
            {
                if (!IsOwnedStagingItem(item) || item.LastModified is not { } modified || modified >= cutoffUtc)
                {
                    continue;
                }

                _ = await storage.DeleteAsync(
                        item.Path,
                        new StorageDeleteOptions { IgnoreMissing = true },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            continuationToken = page.Value.ContinuationToken;
            if (continuationToken is null)
            {
                return;
            }

            if (!observedTokens.Add(continuationToken))
            {
                return;
            }
        }
    }

    private static bool IsOwnedStagingItem(StorageItem item)
    {
        if (item.ItemType != StorageItemType.File ||
            !item.Path.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = item.Path[StagingPrefix.Length..];
        const int randomHexLength = 64;
        const string suffix = ".partial";
        if (fileName.Length != randomHexLength + suffix.Length ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal) ||
            fileName[..randomHexLength].Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            return false;
        }

        return true;
    }
}
