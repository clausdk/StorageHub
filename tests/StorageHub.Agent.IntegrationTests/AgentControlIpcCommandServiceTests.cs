using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.IntegrationTests;

public sealed class AgentControlIpcCommandServiceTests
{
    [Fact]
    public async Task Shutdown_is_acknowledged_before_the_agent_lifetime_is_signaled()
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateServerOptions();
        var handler = new AgentIpcRequestHandler(
            () => throw new InvalidOperationException("Status was not requested."),
            new AgentControlIpcCommandService(
                () => shutdown.TrySetResult(),
                Environment.ProcessId));
        await using var server = new NamedPipeIpcServerSubsystem(options, handler.HandleSessionAsync);
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        _ = await client.ConnectAsync();

        var requestId = Guid.NewGuid();
        await client.SendAsync(IpcEnvelope.Create(
            AgentControlIpcMessageTypes.ShutdownRequest,
            requestId,
            sequence: 1,
            new AgentShutdownRequest(
                AgentControlIpcContract.CurrentVersion,
                AgentShutdownReasons.Update)));
        var response = await client.ReceiveAsync();

        Assert.Equal(AgentControlIpcMessageTypes.ShutdownResponse, response.MessageType);
        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(
            new AgentShutdownResponse(
                AgentControlIpcContract.CurrentVersion,
                Accepted: true,
                Environment.ProcessId),
            response.DeserializePayload<AgentShutdownResponse>());
        await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Invalid_shutdown_reason_fails_closed_without_signaling_lifetime()
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateServerOptions();
        var handler = new AgentIpcRequestHandler(
            () => throw new InvalidOperationException("Status was not requested."),
            new AgentControlIpcCommandService(
                () => shutdown.TrySetResult(),
                Environment.ProcessId));
        await using var server = new NamedPipeIpcServerSubsystem(options, handler.HandleSessionAsync);
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        _ = await client.ConnectAsync();

        await client.SendAsync(IpcEnvelope.Create(
            AgentControlIpcMessageTypes.ShutdownRequest,
            Guid.NewGuid(),
            sequence: 1,
            new AgentShutdownRequest(AgentControlIpcContract.CurrentVersion, "arbitrary")));
        var response = await client.ReceiveAsync();

        Assert.Equal(IpcProtocol.ErrorResponseMessageType, response.MessageType);
        Assert.Equal(
            "agent.control.request.invalid",
            response.DeserializePayload<IpcErrorResponse>().Code);
        Assert.False(shutdown.Task.IsCompleted);
    }

    private static NamedPipeIpcServerOptions CreateServerOptions() => new()
    {
        PipeName = $"storagehub-control-tests-{Guid.NewGuid():N}",
        AgentInstanceId = Guid.NewGuid(),
        AgentVersion = "1.0.0-tests",
        HandshakeTimeout = TimeSpan.FromSeconds(2)
    };

    private static NamedPipeIpcClientOptions CreateClientOptions(string pipeName) => new()
    {
        PipeName = pipeName,
        ClientName = "StorageHub.Control.Tests",
        ClientVersion = "1.0.0-tests",
        ClientInstanceId = Guid.NewGuid(),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        MaxConnectAttempts = 1
    };

    private static async Task StartAsync(NamedPipeIpcServerSubsystem server)
    {
        var result = await server.InitializeAsync(CancellationToken.None);
        Assert.True(result.IsReady);
        await server.StartAsync(CancellationToken.None);
    }
}
