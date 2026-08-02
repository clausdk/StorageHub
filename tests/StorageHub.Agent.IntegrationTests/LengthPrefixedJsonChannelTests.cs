using System.Buffers.Binary;
using StorageHub.Agent.Ipc;

namespace StorageHub.Agent.IntegrationTests;

public sealed class LengthPrefixedJsonChannelTests
{
    [Fact]
    public async Task RoundTripsVersionedMessage()
    {
        await using var stream = new MemoryStream();
        var expected = new TestMessage(1, "hello");

        await LengthPrefixedJsonChannel.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await LengthPrefixedJsonChannel.ReadAsync<TestMessage>(stream);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RejectsOversizedOutgoingPayload()
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await LengthPrefixedJsonChannel.WriteAsync(stream, new TestMessage(1, new string('x', 200)), 32));
    }

    [Fact]
    public async Task RejectsClaimedOversizedIncomingPayloadBeforeAllocation()
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, 4096);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await LengthPrefixedJsonChannel.ReadAsync<TestMessage>(stream, 128));
    }

    [Fact]
    public async Task RejectsTruncatedPayload()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), 20);
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await LengthPrefixedJsonChannel.ReadAsync<TestMessage>(stream));
    }

    private sealed record TestMessage(int Version, string Text);
}
