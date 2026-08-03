namespace StorageHub.Contracts.Ipc;

public static class EditableFileIpcContract
{
    public const int CurrentVersion = 1;
    public const int MaximumContentBytes = 1_048_576;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class EditableFileIpcMessageTypes
{
    public const string DownloadRequest = "editable-file.download.request";
    public const string DownloadResponse = "editable-file.download.response";
    public const string UploadRequest = "editable-file.upload.request";
    public const string UploadResponse = "editable-file.upload.response";
    public const string DirectoryEnsureRequest = "storage-directory.ensure.request";
    public const string DirectoryEnsureResponse = "storage-directory.ensure.response";
}

public sealed record EditableFileDownloadRequest(
    int ContractVersion,
    ObjectInspectorAddress Address,
    int MaximumBytes)
{
    public bool HasValidBounds =>
        EditableFileIpcContract.IsSupported(ContractVersion) &&
        Address?.HasValidBounds == true &&
        MaximumBytes is >= 1 and <= EditableFileIpcContract.MaximumContentBytes;
}

public sealed record EditableFileDownloadResponse(
    int ContractVersion,
    ObjectInspectorAddress Address,
    byte[] Content,
    string? ContentType = null,
    StorageIpcFailure? Failure = null);

public sealed record EditableFileUploadRequest(
    int ContractVersion,
    ObjectInspectorAddress Address,
    byte[] Content,
    string? ContentType = null)
{
    public bool HasValidBounds =>
        EditableFileIpcContract.IsSupported(ContractVersion) &&
        Address?.HasValidBounds == true &&
        Content is { Length: <= EditableFileIpcContract.MaximumContentBytes } &&
        (ContentType is null ||
            !string.IsNullOrWhiteSpace(ContentType) &&
            ContentType.Length <= StorageIpcLimits.MaximumContentTypeLength &&
            !ContentType.Any(char.IsControl));
}

public sealed record EditableFileUploadResponse(
    int ContractVersion,
    ObjectInspectorAddress Address,
    long Size,
    DateTimeOffset? LastModifiedUtc,
    StorageIpcFailure? Failure = null);

public sealed record StorageDirectoryEnsureRequest(
    int ContractVersion,
    ObjectInspectorAddress Address)
{
    public bool HasValidBounds =>
        EditableFileIpcContract.IsSupported(ContractVersion) &&
        Address?.HasValidBounds == true &&
        Address.NativeItemId is null &&
        Address.VersionId is null &&
        Address.EntityTag is null;
}

public sealed record StorageDirectoryEnsureResponse(
    int ContractVersion,
    ObjectInspectorAddress Address,
    bool Created,
    StorageIpcFailure? Failure = null);
