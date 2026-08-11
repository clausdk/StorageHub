using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>Bounded Windows-shell import review protocol. Shell paths never become executable
/// work until a review token has been explicitly committed.</summary>
public static class ShellTransferIpcContract
{
    public const int CurrentVersion = 1;
    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class ShellTransferIpcMessageTypes
{
    public const string PlanImportRequest = "shell-transfer.plan-import.request";
    public const string PlanImportResponse = "shell-transfer.plan-import.response";
    public const string CommitImportRequest = "shell-transfer.commit-import.request";
    public const string CommitImportResponse = "shell-transfer.commit-import.response";
}

public static class ShellTransferIpcLimits
{
    public const int MaximumPaths = 256;
    public const int MaximumEntries = 10_000;
    public const int MaximumPathLength = 32_768;
    public const int MaximumReviewTokenLength = 128;
}

[JsonConverter(typeof(JsonStringEnumConverter<ShellImportDisposition>))]
public enum ShellImportDisposition { ReplaceFiles, SkipConflictingFiles, Cancel }

public sealed record ShellImportPlanRequest(
    int ContractVersion,
    string[] SourcePaths,
    TransferQueueAddress Destination)
{
    public bool HasValidBounds => ContractVersion > 0 && SourcePaths is { Length: > 0 and <= ShellTransferIpcLimits.MaximumPaths } &&
        SourcePaths.All(path => !string.IsNullOrWhiteSpace(path) && path.Length <= ShellTransferIpcLimits.MaximumPathLength && !path.Any(char.IsControl)) &&
        Destination is not null && Destination.HasValidBounds;
}

public sealed record ShellImportItem(string RelativePath, bool IsDirectory, long? Length, bool DestinationConflict);

public sealed record ShellImportPlanResponse(
    int ContractVersion,
    string? ReviewToken,
    ShellImportItem[] Items,
    StorageIpcFailure? Failure = null);

public sealed record ShellImportCommitRequest(int ContractVersion, string ReviewToken, ShellImportDisposition Disposition)
{
    public bool HasValidBounds => ContractVersion > 0 && Enum.IsDefined(Disposition) &&
        !string.IsNullOrWhiteSpace(ReviewToken) && ReviewToken.Length <= ShellTransferIpcLimits.MaximumReviewTokenLength && !ReviewToken.Any(char.IsControl);
}

public sealed record ShellImportCommitResponse(
    int ContractVersion,
    bool Accepted,
    Guid[] TransferIds,
    StorageIpcFailure? Failure = null);
