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
    public const string PrepareExportRequest = "shell-transfer.prepare-export.request";
    public const string PrepareExportResponse = "shell-transfer.prepare-export.response";
    public const string StartExportRequest = "shell-transfer.start-export.request";
    public const string StartExportResponse = "shell-transfer.start-export.response";
    public const string ExportStatusRequest = "shell-transfer.export-status.request";
    public const string ExportStatusResponse = "shell-transfer.export-status.response";
    public const string BeginExplorerDropRequest = "shell-transfer.begin-explorer-drop.request";
    public const string BeginExplorerDropResponse = "shell-transfer.begin-explorer-drop.response";
    public const string CommitExplorerDropRequest = "shell-transfer.commit-explorer-drop.request";
    public const string CommitExplorerDropResponse = "shell-transfer.commit-explorer-drop.response";
}

public sealed record ShellExportSource(TransferQueueAddress Address, bool IsDirectory, string DisplayName)
{
    public bool HasValidBounds => Address is not null && Address.HasValidBounds &&
        !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Length <= 255 && !DisplayName.Any(char.IsControl);
}

public sealed record ShellExportPrepareRequest(int ContractVersion, ShellExportSource[] Sources)
{
    public bool HasValidBounds => ContractVersion > 0 && Sources is { Length: > 0 and <= ShellTransferIpcLimits.MaximumPaths } &&
        Sources.All(source => source is not null && source.HasValidBounds);
}

public sealed record ShellExportPrepareResponse(
    int ContractVersion,
    string[] LocalPaths,
    StorageIpcFailure? Failure = null);

public sealed record ShellExportStartResponse(
    int ContractVersion,
    Guid ExportId,
    StorageIpcFailure? Failure = null);

public sealed record ShellExportStatusRequest(int ContractVersion, Guid ExportId)
{
    public bool HasValidBounds => ContractVersion > 0 && ExportId != Guid.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter<ShellExportState>))]
public enum ShellExportState { Queued, Discovering, Transferring, Completed, Failed }

public sealed record ShellExportStatusResponse(
    int ContractVersion,
    Guid ExportId,
    ShellExportState State,
    int DiscoveredEntries,
    int CompletedFiles,
    long CompletedBytes,
    string[] LocalPaths,
    StorageIpcFailure? Failure = null);

public sealed record ExplorerDropBeginResponse(
    int ContractVersion,
    string? DropToken,
    string? MarkerPath,
    StorageIpcFailure? Failure = null);

public sealed record ExplorerDropCommitRequest(int ContractVersion, string DropToken)
{
    public bool HasValidBounds => ContractVersion > 0 &&
        DropToken is { Length: 32 } && DropToken.All(Uri.IsHexDigit);
}

public sealed record ExplorerDropCommitResponse(
    int ContractVersion,
    bool Accepted,
    Guid ExportId,
    string? DestinationPath,
    StorageIpcFailure? Failure = null);

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
