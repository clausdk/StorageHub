using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public interface ISshTerminalAgentClient : IAsyncDisposable
{
    Task<SshTerminalOpenResponse> OpenAsync(SshTerminalOpenRequest request, CancellationToken cancellationToken = default);
    Task<SshTerminalWriteResponse> WriteAsync(SshTerminalWriteRequest request, CancellationToken cancellationToken = default);
    Task<SshTerminalReadResponse> ReadAsync(SshTerminalReadRequest request, CancellationToken cancellationToken = default);
    Task<SshTerminalResizeResponse> ResizeAsync(SshTerminalResizeRequest request, CancellationToken cancellationToken = default);
    Task<SshTerminalCloseResponse> CloseAsync(SshTerminalCloseRequest request, CancellationToken cancellationToken = default);
}

public sealed class NamedPipeSshTerminalAgentClient : ISshTerminalAgentClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(35);
    private bool _disposed;

    public Task<SshTerminalOpenResponse> OpenAsync(
        SshTerminalOpenRequest request,
        CancellationToken cancellationToken = default) => ExecuteAsync<SshTerminalOpenRequest, SshTerminalOpenResponse>(
        SshTerminalIpcMessageTypes.OpenRequest,
        SshTerminalIpcMessageTypes.OpenResponse,
        request,
        cancellationToken);

    public Task<SshTerminalWriteResponse> WriteAsync(
        SshTerminalWriteRequest request,
        CancellationToken cancellationToken = default) => ExecuteAsync<SshTerminalWriteRequest, SshTerminalWriteResponse>(
        SshTerminalIpcMessageTypes.WriteRequest,
        SshTerminalIpcMessageTypes.WriteResponse,
        request,
        cancellationToken);

    public Task<SshTerminalReadResponse> ReadAsync(
        SshTerminalReadRequest request,
        CancellationToken cancellationToken = default) => ExecuteAsync<SshTerminalReadRequest, SshTerminalReadResponse>(
        SshTerminalIpcMessageTypes.ReadRequest,
        SshTerminalIpcMessageTypes.ReadResponse,
        request,
        cancellationToken);

    public Task<SshTerminalResizeResponse> ResizeAsync(
        SshTerminalResizeRequest request,
        CancellationToken cancellationToken = default) => ExecuteAsync<SshTerminalResizeRequest, SshTerminalResizeResponse>(
        SshTerminalIpcMessageTypes.ResizeRequest,
        SshTerminalIpcMessageTypes.ResizeResponse,
        request,
        cancellationToken);

    public Task<SshTerminalCloseResponse> CloseAsync(
        SshTerminalCloseRequest request,
        CancellationToken cancellationToken = default) => ExecuteAsync<SshTerminalCloseRequest, SshTerminalCloseResponse>(
        SshTerminalIpcMessageTypes.CloseRequest,
        SshTerminalIpcMessageTypes.CloseResponse,
        request,
        cancellationToken);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        string requestType,
        string responseType,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await using var client = new NamedPipeIpcClient(new NamedPipeIpcClientOptions
        {
            PipeName = AgentStatusMonitor.DefaultPipeName,
            ClientName = "StorageHub.Desktop.SshTerminal",
            ClientVersion = DesktopApplicationVersion.Current,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxConnectAttempts = 2,
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaximumReconnectDelay = TimeSpan.FromMilliseconds(250)
        });
        _ = await client.ConnectAsync(lifetime.Token).ConfigureAwait(false);
        var requestId = Guid.NewGuid();
        await client.SendAsync(IpcEnvelope.Create(requestType, requestId, 1, request), lifetime.Token)
            .ConfigureAwait(false);
        var envelope = await client.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
        if (envelope.RequestId != requestId ||
            !string.Equals(envelope.MessageType, responseType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The agent returned an unexpected SSH terminal response.");
        }
        return envelope.DeserializePayload<TResponse>();
    }
}
