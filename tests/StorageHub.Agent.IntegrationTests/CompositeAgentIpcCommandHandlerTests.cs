using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.IntegrationTests;

public sealed class CompositeAgentIpcCommandHandlerTests
{
    [Fact]
    public async Task Composite_routes_each_message_to_its_single_owner()
    {
        var storage = new RecordingHandler("storage.list.request", "storage.list.response");
        var transfers = new RecordingHandler("transfer.list.request", "transfer.list.response");
        var composite = new CompositeAgentIpcCommandHandler(storage, transfers);
        var request = IpcEnvelope.Create(
            "transfer.list.request",
            Guid.NewGuid(),
            sequence: 1,
            new { });

        var response = await composite.HandleAsync(request);

        Assert.True(composite.CanHandle("storage.list.request"));
        Assert.True(composite.CanHandle("transfer.list.request"));
        Assert.Equal("transfer.list.response", response.MessageType);
        Assert.Equal(0, storage.CallCount);
        Assert.Equal(1, transfers.CallCount);
    }

    [Fact]
    public async Task Composite_fails_closed_when_two_handlers_claim_a_message()
    {
        var first = new RecordingHandler("transfer.list.request", "one.response");
        var second = new RecordingHandler("transfer.list.request", "two.response");
        var composite = new CompositeAgentIpcCommandHandler(first, second);
        var request = IpcEnvelope.Create(
            "transfer.list.request",
            Guid.NewGuid(),
            sequence: 1,
            new { });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await composite.HandleAsync(request));
    }

    private sealed class RecordingHandler(string requestType, string responseType)
        : IAgentIpcCommandHandler
    {
        public int CallCount { get; private set; }

        public bool CanHandle(string messageType) =>
            string.Equals(messageType, requestType, StringComparison.Ordinal);

        public ValueTask<AgentIpcCommandResponse> HandleAsync(
            IpcEnvelope request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(AgentIpcCommandResponse.Create(responseType, new { }));
        }
    }
}
