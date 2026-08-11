namespace StorageHub.Contracts.Ipc;

public static class SshTerminalIpcContract
{
    public const int CurrentVersion = 1;
    public const int MaximumChunkBytes = 32 * 1024;
    public const int MaximumTerminalNameLength = 64;
    public const int MinimumColumns = 20;
    public const int MaximumColumns = 500;
    public const int MinimumRows = 5;
    public const int MaximumRows = 200;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class SshTerminalIpcMessageTypes
{
    public const string OpenRequest = "ssh.terminal.open.request";
    public const string OpenResponse = "ssh.terminal.open.response";
    public const string WriteRequest = "ssh.terminal.write.request";
    public const string WriteResponse = "ssh.terminal.write.response";
    public const string ReadRequest = "ssh.terminal.read.request";
    public const string ReadResponse = "ssh.terminal.read.response";
    public const string ResizeRequest = "ssh.terminal.resize.request";
    public const string ResizeResponse = "ssh.terminal.resize.response";
    public const string CloseRequest = "ssh.terminal.close.request";
    public const string CloseResponse = "ssh.terminal.close.response";
}

public sealed record SshTerminalOpenRequest(
    int ContractVersion,
    Guid ConnectionId,
    int Columns = 120,
    int Rows = 30,
    string TerminalName = "xterm-256color")
{
    public bool HasValidBounds =>
        SshTerminalIpcContract.IsSupported(ContractVersion) &&
        ConnectionId != Guid.Empty &&
        ValidSize(Columns, Rows) &&
        !string.IsNullOrWhiteSpace(TerminalName) &&
        TerminalName.Length <= SshTerminalIpcContract.MaximumTerminalNameLength &&
        !TerminalName.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character));

    internal static bool ValidSize(int columns, int rows) =>
        columns is >= SshTerminalIpcContract.MinimumColumns and <= SshTerminalIpcContract.MaximumColumns &&
        rows is >= SshTerminalIpcContract.MinimumRows and <= SshTerminalIpcContract.MaximumRows;
}

public sealed record SshTerminalOpenResponse(
    int ContractVersion,
    Guid SessionId,
    string DisplayName,
    StorageIpcFailure? Failure = null);

public sealed record SshTerminalWriteRequest(int ContractVersion, Guid SessionId, byte[] Content)
{
    public bool HasValidBounds =>
        SshTerminalIpcContract.IsSupported(ContractVersion) &&
        SessionId != Guid.Empty &&
        Content is { Length: > 0 and <= SshTerminalIpcContract.MaximumChunkBytes };
}

public sealed record SshTerminalWriteResponse(
    int ContractVersion,
    Guid SessionId,
    int AcceptedBytes,
    StorageIpcFailure? Failure = null);

public sealed record SshTerminalReadRequest(int ContractVersion, Guid SessionId, int MaximumBytes)
{
    public bool HasValidBounds =>
        SshTerminalIpcContract.IsSupported(ContractVersion) &&
        SessionId != Guid.Empty &&
        MaximumBytes is >= 1 and <= SshTerminalIpcContract.MaximumChunkBytes;
}

public sealed record SshTerminalReadResponse(
    int ContractVersion,
    Guid SessionId,
    byte[] Content,
    bool IsConnected,
    StorageIpcFailure? Failure = null);

public sealed record SshTerminalResizeRequest(
    int ContractVersion,
    Guid SessionId,
    int Columns,
    int Rows)
{
    public bool HasValidBounds =>
        SshTerminalIpcContract.IsSupported(ContractVersion) &&
        SessionId != Guid.Empty &&
        SshTerminalOpenRequest.ValidSize(Columns, Rows);
}

public sealed record SshTerminalResizeResponse(
    int ContractVersion,
    Guid SessionId,
    bool Resized,
    StorageIpcFailure? Failure = null);

public sealed record SshTerminalCloseRequest(int ContractVersion, Guid SessionId)
{
    public bool HasValidBounds => SshTerminalIpcContract.IsSupported(ContractVersion) && SessionId != Guid.Empty;
}

public sealed record SshTerminalCloseResponse(
    int ContractVersion,
    Guid SessionId,
    bool Closed,
    StorageIpcFailure? Failure = null);
