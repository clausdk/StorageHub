using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ConnectionManagementAgentClientTests
{
    [Fact]
    public async Task ProfileClientSendsVersionedCreateAndValidatesReturnedProfile()
    {
        var draft = LocalDraft();
        var connectionId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var transport = new FakeProfileTransport(request => IpcEnvelope.Create(
            ConnectionProfileIpcMessageTypes.CreateResponse,
            request.RequestId,
            1,
            new ConnectionProfileWriteResponse(
                ConnectionProfileIpcContract.CurrentVersion,
                ConnectionProfileWriteStatus.Succeeded,
                new ConnectionProfileDocument(connectionId, 1, draft, now, now),
                ActualVersion: 1)));
        await using var client = new NamedPipeRemoteConnectionProfileClient(transport);

        var response = await client.CreateAsync(new ConnectionProfileCreateRequest(
            ConnectionProfileIpcContract.CurrentVersion,
            draft));

        Assert.Equal(connectionId, response.Profile?.ConnectionId);
        Assert.Equal(ConnectionProfileIpcMessageTypes.CreateRequest, transport.LastRequest?.MessageType);
    }

    [Fact]
    public async Task ProfileClientRejectsSuccessWithoutMatchingVersionedProfile()
    {
        var draft = LocalDraft();
        var transport = new FakeProfileTransport(request => IpcEnvelope.Create(
            ConnectionProfileIpcMessageTypes.UpdateResponse,
            request.RequestId,
            1,
            new ConnectionProfileWriteResponse(
                ConnectionProfileIpcContract.CurrentVersion,
                ConnectionProfileWriteStatus.Succeeded,
                Profile: null,
                ActualVersion: 3)));
        await using var client = new NamedPipeRemoteConnectionProfileClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.UpdateAsync(
            new ConnectionProfileUpdateRequest(
                ConnectionProfileIpcContract.CurrentVersion,
                Guid.NewGuid(),
                ExpectedVersion: 2,
                draft)));

        Assert.Equal(1, transport.DisconnectCount);
    }

    [Fact]
    public async Task SecretClientCopiesAndZerosOutboundMaterialAndCorrelatesResponse()
    {
        byte[]? observedMaterial = null;
        var transport = new FakeSecretTransport(request =>
        {
            observedMaterial = request.Payload.SecretMaterial;
            return new SecretIpcResponseEnvelope(
                SecretVaultIpcMessageTypes.EnrollResponse,
                request.RequestId,
                1,
                new SecretVaultResponse(
                    SecretVaultIpcContract.CurrentVersion,
                    SecretVaultOperation.Enroll,
                    Succeeded: true,
                    "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                    Version: 1));
        });
        await using var client = new NamedPipeRemoteSecretVaultClient(transport);
        var callerOwned = new byte[] { 5, 6, 7 };

        var response = await client.EnrollAsync(SecretMaterialPurpose.Password, callerOwned);

        Assert.True(response.Succeeded);
        Assert.Equal([5, 6, 7], callerOwned);
        Assert.NotNull(observedMaterial);
        Assert.All(observedMaterial, value => Assert.Equal(0, value));
        Assert.Equal(SecretVaultIpcMessageTypes.EnrollRequest, transport.LastRequest?.MessageType);
    }

    [Fact]
    public async Task SecretClientRejectsNullTypedResponsePayloadAndDisconnects()
    {
        var transport = new FakeSecretTransport(request => new SecretIpcResponseEnvelope(
            SecretVaultIpcMessageTypes.EnrollResponse,
            request.RequestId,
            1,
            Payload: null!));
        await using var client = new NamedPipeRemoteSecretVaultClient(transport);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.EnrollAsync(
            SecretMaterialPurpose.Password,
            new byte[] { 1 }));

        Assert.False(transport.IsConnected);
    }

    private static ConnectionProfileDraft LocalDraft() => new(
        new ConnectionProfileMetadataDocument("Local files", Tags: []),
        new ConnectionEndpointDocument(StorageConnectionProvider.Local, RootPath: "C:\\Data"),
        new ConnectionAuthenticationDocument(ConnectionAuthenticationKind.None),
        new ConnectionOperationalOptionsDocument());

    private sealed class FakeProfileTransport(Func<IpcEnvelope, IpcEnvelope> responseFactory)
        : IStorageIpcTransport
    {
        public bool IsConnected { get; private set; }
        public int DisconnectCount { get; private set; }
        public IpcEnvelope? LastRequest { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken = default)
        {
            LastRequest = envelope;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IpcEnvelope> ReceiveAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(responseFactory(LastRequest!));

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSecretTransport(
        Func<SecretIpcRequestEnvelope, SecretIpcResponseEnvelope> responseFactory)
        : ISecretIpcTransport
    {
        public bool IsConnected { get; private set; }
        public SecretIpcRequestEnvelope? LastRequest { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            SecretIpcRequestEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            LastRequest = envelope;
            return ValueTask.CompletedTask;
        }

        public ValueTask<SecretIpcResponseEnvelope> ReceiveAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(responseFactory(LastRequest!));

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
