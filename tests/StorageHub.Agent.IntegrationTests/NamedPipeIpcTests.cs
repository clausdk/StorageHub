using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.IntegrationTests;

public sealed class NamedPipeIpcTests
{
    [Fact]
    public async Task EstablishedSessionsAllowBoundedLongRunningStorageRequests()
    {
        var options = CreateServerOptions() with
        {
            RequestTimeout = TimeSpan.FromMinutes(2),
            SessionIdleTimeout = TimeSpan.FromMinutes(3)
        };

        await using var server = new NamedPipeIpcServerSubsystem(options);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NamedPipeIpcServerSubsystem(options with
            {
                RequestTimeout = TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1)
            }));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NamedPipeIpcServerSubsystem(options with
            {
                HandshakeTimeout = TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1)
            }));
    }

    [Fact]
    public async Task NormalAndSecretServersHaveDistinctSubsystemNames()
    {
        var normalOptions = CreateServerOptions();
        await using var normal = new NamedPipeIpcServerSubsystem(normalOptions);
        await using var secret = new NamedPipeIpcServerSubsystem(normalOptions with
        {
            PipeName = $"storagehub-secret-tests-{Guid.NewGuid():N}",
            FrameKind = IpcFrameKind.Secret
        });

        Assert.NotEqual(normal.Name, secret.Name);
        _ = new AgentRuntimeCoordinator([normal, secret]);
    }

    [Fact]
    public async Task NegotiatesProtocolAndRoundTripsNormalEnvelope()
    {
        var options = CreateServerOptions();
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            static async (session, cancellationToken) =>
            {
                var request = await session.ReceiveAsync(cancellationToken);
                var payload = request.DeserializePayload<TestPayload>();
                await session.SendAsync(
                    IpcEnvelope.Create(
                        "test.response",
                        request.RequestId,
                        request.Sequence + 1,
                        new TestPayload(payload.Value + "-received")),
                    cancellationToken);
            });
        await StartAsync(server);

        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        var hello = await client.ConnectAsync();
        var requestId = Guid.NewGuid();
        await client.SendAsync(
            IpcEnvelope.Create("test.request", requestId, 1, new TestPayload("hello")));
        var response = await client.ReceiveAsync();

        Assert.True(hello.Accepted);
        Assert.Equal(ProtocolVersion.Current, hello.ProtocolVersion);
        Assert.Equal(options.AgentInstanceId, hello.AgentInstanceId);
        Assert.Equal("test.response", response.MessageType);
        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(2, response.Sequence);
        Assert.Equal(new TestPayload("hello-received"), response.DeserializePayload<TestPayload>());
        Assert.Equal(OperatingSystem.IsWindows(), NamedPipeIpcServerSubsystem.UsesCurrentUserOnlySecurity);
        Assert.Equal(OperatingSystem.IsWindows(), NamedPipeIpcClient.UsesCurrentUserOnlySecurity);
    }

    [Fact]
    public async Task RejectsIncompatibleMajorProtocolVersion()
    {
        var options = CreateServerOptions();
        await using var server = new NamedPipeIpcServerSubsystem(options);
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName) with
        {
            ProtocolVersion = new ProtocolVersion(2, 0),
            MaxConnectAttempts = 1
        });

        var error = await Assert.ThrowsAsync<IpcProtocolNegotiationException>(
            async () => await client.ConnectAsync());

        Assert.Contains("1.0", error.Message, StringComparison.Ordinal);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task RetriesConnectionAndPerformsFreshHandshakeWhenServerAppears()
    {
        var options = CreateServerOptions();
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            static (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName) with
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(40),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaximumReconnectDelay = TimeSpan.FromMilliseconds(20),
            MaxConnectAttempts = 20
        });

        var connecting = client.ConnectAsync();
        await Task.Delay(100);
        await StartAsync(server);
        var hello = await connecting;

        Assert.True(hello.Accepted);
        Assert.True(client.IsConnected);
        Assert.True(client.LastConnectionAttemptCount > 1);
    }

    [Fact]
    public async Task BoundsConcurrentAuthenticatedClients()
    {
        const int maximumClients = 2;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new ConcurrentQueue<Guid>();
        var options = CreateServerOptions() with { MaxConcurrentClients = maximumClients };
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            async (session, cancellationToken) =>
            {
                entered.Enqueue(session.ClientHello.ClientInstanceId);
                await release.Task.WaitAsync(cancellationToken);
            });
        await StartAsync(server);
        await using var first = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await using var second = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await using var third = new NamedPipeIpcClient(CreateClientOptions(options.PipeName) with
        {
            MaxConnectAttempts = 1,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        });

        await Task.WhenAll(first.ConnectAsync(), second.ConnectAsync());
        await WaitUntilAsync(() => server.ActiveClientCount == maximumClients, TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await third.ConnectAsync(timeout.Token));

        Assert.Equal(maximumClients, server.ActiveClientCount);
        Assert.Equal(maximumClients, server.PeakClientCount);
        Assert.Equal(maximumClients, entered.Count);
        release.TrySetResult();
    }

    [Fact]
    public async Task NormalChannelRejectsSecretMessageTypes()
    {
        var options = CreateServerOptions();
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            static (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await client.ConnectAsync();

        var secretEnvelope = IpcEnvelope.Create(
            "secret.enrollment",
            Guid.NewGuid(),
            1,
            new TestPayload("must-not-cross-normal-channel"));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await client.SendAsync(secretEnvelope));
    }

    [Fact]
    public async Task DedicatedSecretChannelRoundTripsOnlyTypedBoundedSecretEnvelopes()
    {
        var options = CreateServerOptions() with { FrameKind = IpcFrameKind.Secret };
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            static async (session, cancellationToken) =>
            {
                var request = await session.ReceiveSecretAsync(cancellationToken);
                Assert.Equal(SecretVaultIpcMessageTypes.EnrollRequest, request.MessageType);
                Assert.Equal([1, 2, 3], request.Payload.SecretMaterial);
                await session.SendSecretAsync(
                    new SecretIpcResponseEnvelope(
                        SecretVaultIpcMessageTypes.EnrollResponse,
                        request.RequestId,
                        1,
                        new SecretVaultResponse(
                            SecretVaultIpcContract.CurrentVersion,
                            SecretVaultOperation.Enroll,
                            Succeeded: true,
                            "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                            Version: 1)),
                    cancellationToken);
            });
        await StartAsync(server);

        await using var client = new NamedPipeIpcClient(
            CreateClientOptions(options.PipeName) with { FrameKind = IpcFrameKind.Secret });
        await client.ConnectAsync();
        var requestId = Guid.NewGuid();
        await client.SendSecretAsync(new SecretIpcRequestEnvelope(
            SecretVaultIpcMessageTypes.EnrollRequest,
            requestId,
            1,
            new SecretVaultRequest(
                SecretVaultIpcContract.CurrentVersion,
                SecretVaultOperation.Enroll,
                SecretMaterialPurpose.Password,
                Reference: null,
                SecretMaterial: [1, 2, 3])));

        var response = await client.ReceiveSecretAsync();

        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(SecretVaultIpcMessageTypes.EnrollResponse, response.MessageType);
        Assert.True(response.Payload.Succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.SendAsync(IpcEnvelope.Create("test.request", Guid.NewGuid(), 2, new TestPayload("no"))));
    }

    [Fact]
    public async Task SecretRequestHandlerZerosMaterialAfterDedicatedCommandCompletes()
    {
        var command = new RecordingSecretCommandHandler();
        var handler = new AgentSecretIpcRequestHandler(command);
        var options = CreateServerOptions() with { FrameKind = IpcFrameKind.Secret };
        await using var server = new NamedPipeIpcServerSubsystem(options, handler.HandleSessionAsync);
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(
            CreateClientOptions(options.PipeName) with { FrameKind = IpcFrameKind.Secret });
        await client.ConnectAsync();
        var requestId = Guid.NewGuid();
        await client.SendSecretAsync(new SecretIpcRequestEnvelope(
            SecretVaultIpcMessageTypes.EnrollRequest,
            requestId,
            1,
            new SecretVaultRequest(
                SecretVaultIpcContract.CurrentVersion,
                SecretVaultOperation.Enroll,
                SecretMaterialPurpose.Password,
                Reference: null,
                SecretMaterial: [9, 8, 7])));

        var response = await client.ReceiveSecretAsync();

        Assert.Equal(requestId, response.RequestId);
        Assert.NotNull(command.Material);
        Assert.All(command.Material, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task StopCancelsSessionsAndLeavesSubsystemHealthyToDispose()
    {
        var sessionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateServerOptions();
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            async (_, cancellationToken) =>
            {
                sessionStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    sessionCancelled.TrySetResult();
                }
            });
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await client.ConnectAsync();
        await sessionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await server.StopAsync(CancellationToken.None);

        await sessionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(server.IsRunning);
        Assert.Equal(0, server.ActiveClientCount);
        var health = await server.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(SubsystemHealthLevel.Degraded, health.Level);
    }

    [Fact]
    public async Task ClientDisposalUnblocksPendingReceive()
    {
        var options = CreateServerOptions();
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            static (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        await StartAsync(server);
        var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await client.ConnectAsync();
        var pendingReceive = client.ReceiveAsync().AsTask();

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<Exception>(async () => await pendingReceive);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task IdleSessionIsClosedWithoutPoisoningSubsystemHealth()
    {
        var options = CreateServerOptions() with
        {
            SessionIdleTimeout = TimeSpan.FromMilliseconds(100),
            RequestTimeout = TimeSpan.FromSeconds(1)
        };
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            static async (session, cancellationToken) =>
                _ = await session.ReceiveAsync(cancellationToken));
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await client.ConnectAsync();
        await WaitUntilAsync(() => server.ActiveClientCount == 1, TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => server.ActiveClientCount == 0, TimeSpan.FromSeconds(5));

        var health = await server.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(SubsystemHealthLevel.Healthy, health.Level);
    }

    [Fact]
    public async Task RequestDeadlineCancelsOnlyTheSlowSession()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateServerOptions() with
        {
            SessionIdleTimeout = TimeSpan.FromSeconds(1),
            RequestTimeout = TimeSpan.FromMilliseconds(100)
        };
        await using var server = new NamedPipeIpcServerSubsystem(
            options,
            async (session, cancellationToken) =>
            {
                _ = await session.ReceiveAsync(cancellationToken);
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await client.ConnectAsync();
        await client.SendAsync(IpcEnvelope.Create(
            "test.request",
            Guid.NewGuid(),
            1,
            new TestPayload("slow")));
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(() => server.ActiveClientCount == 0, TimeSpan.FromSeconds(5));

        var health = await server.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(SubsystemHealthLevel.Healthy, health.Level);
    }

    [Fact]
    public async Task MalformedClientFrameIsSessionLocalAndServerContinuesAccepting()
    {
        var options = CreateServerOptions();
        await using var server = new NamedPipeIpcServerSubsystem(options);
        await StartAsync(server);
        var pipeOptions = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
        {
            pipeOptions |= PipeOptions.CurrentUserOnly;
        }

        await using (var malformed = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            pipeOptions))
        {
            await malformed.ConnectAsync();
            var invalidHeader = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(invalidHeader, IpcFrameLimits.NormalMaxBytes + 1);
            await malformed.WriteAsync(invalidHeader);
            await malformed.FlushAsync();
        }

        await Task.Delay(100);
        var health = await server.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(SubsystemHealthLevel.Healthy, health.Level);
        await using var valid = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        Assert.True((await valid.ConnectAsync()).Accepted);
    }

    [Fact]
    public async Task MissingHandshakePayloadIsSessionLocalAndServerContinuesAccepting()
    {
        var options = CreateServerOptions();
        await using var server = new NamedPipeIpcServerSubsystem(options);
        await StartAsync(server);
        var pipeOptions = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
        {
            pipeOptions |= PipeOptions.CurrentUserOnly;
        }

        await using (var malformed = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            pipeOptions))
        {
            await malformed.ConnectAsync();
            await LengthPrefixedJsonChannel.WriteAsync(
                malformed,
                new
                {
                    MessageType = IpcProtocol.HelloRequestMessageType,
                    RequestId = Guid.NewGuid(),
                    Sequence = 0
                });
        }

        await Task.Delay(100);
        var health = await server.CheckHealthAsync(CancellationToken.None);
        Assert.Equal(SubsystemHealthLevel.Healthy, health.Level);
        await using var valid = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        Assert.True((await valid.ConnectAsync()).Accepted);
    }

    [Fact]
    public async Task AgentRequestHandlerReturnsStatusAndSafeUnsupportedError()
    {
        var options = CreateServerOptions();
        var snapshot = new AgentStatusSnapshot(
            options.AgentInstanceId,
            AgentLifecycleState.Ready,
            DateTimeOffset.UtcNow,
            ActiveTransfers: 2,
            ActiveSyncRuns: 1,
            "Ready");
        var handler = new AgentIpcRequestHandler(() => snapshot);
        await using var server = new NamedPipeIpcServerSubsystem(options, handler.HandleSessionAsync);
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await client.ConnectAsync();

        var statusRequestId = Guid.NewGuid();
        await client.SendAsync(IpcEnvelope.Create(
            IpcProtocol.AgentStatusRequestMessageType,
            statusRequestId,
            1,
            new AgentStatusRequest()));
        var statusResponse = await client.ReceiveAsync();

        Assert.Equal(IpcProtocol.AgentStatusResponseMessageType, statusResponse.MessageType);
        Assert.Equal(statusRequestId, statusResponse.RequestId);
        Assert.Equal(snapshot, statusResponse.DeserializePayload<AgentStatusSnapshot>());

        var unsupportedRequestId = Guid.NewGuid();
        await client.SendAsync(IpcEnvelope.Create(
            "agent.future.request",
            unsupportedRequestId,
            2,
            new TestPayload("unsupported")));
        var errorResponse = await client.ReceiveAsync();

        Assert.Equal(IpcProtocol.ErrorResponseMessageType, errorResponse.MessageType);
        Assert.Equal(unsupportedRequestId, errorResponse.RequestId);
        Assert.Equal(
            "ipc.message.unsupported",
            errorResponse.DeserializePayload<IpcErrorResponse>().Code);
    }

    [Fact]
    public async Task AgentRequestHandlerDispatchesInjectedReadOnlyCommand()
    {
        var options = CreateServerOptions();
        var handler = new AgentIpcRequestHandler(
            () => throw new InvalidOperationException("Status was not requested."),
            new EchoCommandHandler());
        await using var server = new NamedPipeIpcServerSubsystem(options, handler.HandleSessionAsync);
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await client.ConnectAsync();

        var requestId = Guid.NewGuid();
        await client.SendAsync(IpcEnvelope.Create(
            "storage.test.request",
            requestId,
            1,
            new TestPayload("read-only")));
        var response = await client.ReceiveAsync();

        Assert.Equal("storage.test.response", response.MessageType);
        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(1, response.Sequence);
        Assert.Equal("read-only-ok", response.DeserializePayload<TestPayload>().Value);
    }

    [Fact]
    public async Task FailingInjectedCommandDoesNotDisableRecoveryStatus()
    {
        var options = CreateServerOptions();
        var snapshot = new AgentStatusSnapshot(
            options.AgentInstanceId,
            AgentLifecycleState.Degraded,
            DateTimeOffset.UtcNow,
            ActiveTransfers: 0,
            ActiveSyncRuns: 0,
            "Recovery mode");
        var handler = new AgentIpcRequestHandler(() => snapshot, new ThrowingCommandHandler());
        await using var server = new NamedPipeIpcServerSubsystem(options, handler.HandleSessionAsync);
        await StartAsync(server);
        await using var client = new NamedPipeIpcClient(CreateClientOptions(options.PipeName));
        await client.ConnectAsync();

        await client.SendAsync(IpcEnvelope.Create(
            "storage.test.request",
            Guid.NewGuid(),
            1,
            new TestPayload("fail")));
        var failed = await client.ReceiveAsync();
        Assert.Equal(IpcProtocol.ErrorResponseMessageType, failed.MessageType);
        Assert.Equal("ipc.command.failed", failed.DeserializePayload<IpcErrorResponse>().Code);

        var statusRequestId = Guid.NewGuid();
        await client.SendAsync(IpcEnvelope.Create(
            IpcProtocol.AgentStatusRequestMessageType,
            statusRequestId,
            2,
            new AgentStatusRequest()));
        var status = await client.ReceiveAsync();

        Assert.Equal(IpcProtocol.AgentStatusResponseMessageType, status.MessageType);
        Assert.Equal(statusRequestId, status.RequestId);
        Assert.Equal(snapshot, status.DeserializePayload<AgentStatusSnapshot>());
    }

    private static NamedPipeIpcServerOptions CreateServerOptions() => new()
    {
        PipeName = $"storagehub-tests-{Guid.NewGuid():N}",
        AgentInstanceId = Guid.NewGuid(),
        AgentVersion = "1.0.0-tests",
        HandshakeTimeout = TimeSpan.FromSeconds(2)
    };

    private static NamedPipeIpcClientOptions CreateClientOptions(string pipeName) => new()
    {
        PipeName = pipeName,
        ClientName = "StorageHub.Tests",
        ClientVersion = "1.0.0-tests",
        ClientInstanceId = Guid.NewGuid(),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        MaxConnectAttempts = 3
    };

    private static async Task StartAsync(NamedPipeIpcServerSubsystem server)
    {
        var result = await server.InitializeAsync(CancellationToken.None);
        Assert.True(result.IsReady);
        await server.StartAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private sealed record TestPayload(string Value);

    private sealed class EchoCommandHandler : IAgentIpcCommandHandler
    {
        public bool CanHandle(string messageType) => messageType == "storage.test.request";

        public ValueTask<AgentIpcCommandResponse> HandleAsync(
            IpcEnvelope request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = request.DeserializePayload<TestPayload>();
            return ValueTask.FromResult(AgentIpcCommandResponse.Create(
                "storage.test.response",
                new TestPayload(payload.Value + "-ok")));
        }
    }

    private sealed class ThrowingCommandHandler : IAgentIpcCommandHandler
    {
        public bool CanHandle(string messageType) => messageType == "storage.test.request";

        public ValueTask<AgentIpcCommandResponse> HandleAsync(
            IpcEnvelope request,
            CancellationToken cancellationToken = default) =>
            throw new TestCommandException("sensitive provider detail");
    }

    private sealed class RecordingSecretCommandHandler : IAgentSecretIpcCommandHandler
    {
        public byte[]? Material { get; private set; }

        public bool CanHandle(string messageType) =>
            messageType == SecretVaultIpcMessageTypes.EnrollRequest;

        public ValueTask<AgentSecretIpcCommandResponse> HandleAsync(
            SecretIpcRequestEnvelope request,
            CancellationToken cancellationToken = default)
        {
            Material = request.Payload.SecretMaterial;
            return ValueTask.FromResult(new AgentSecretIpcCommandResponse(
                SecretVaultIpcMessageTypes.EnrollResponse,
                new SecretVaultResponse(
                    SecretVaultIpcContract.CurrentVersion,
                    SecretVaultOperation.Enroll,
                    Succeeded: true,
                    "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    Version: 1)));
        }
    }

    private sealed class TestCommandException(string message) : Exception(message);
}
