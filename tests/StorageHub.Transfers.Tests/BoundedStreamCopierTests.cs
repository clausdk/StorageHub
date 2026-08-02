namespace StorageHub.Transfers.Tests;

public sealed class BoundedStreamCopierTests
{
    [Fact]
    public async Task Copy_is_exact_bounded_and_reports_monotonic_progress()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(index => (byte)(index % 251)).ToArray();
        await using var source = new ReadSizeTrackingStream(bytes);
        await using var destination = new MemoryStream();
        var observations = new List<TransferProgress>();

        var result = await BoundedStreamCopier.CopyAsync(
            source,
            destination,
            expectedLength: bytes.LongLength,
            bufferSize: 4096,
            progress: new InlineProgress<TransferProgress>(observations.Add),
            cancellationToken: CancellationToken.None);

        Assert.Equal(bytes.LongLength, result.BytesCopied);
        Assert.Equal(bytes, destination.ToArray());
        Assert.InRange(source.LargestRequestedRead, 1, 4096);
        Assert.NotEmpty(observations);
        Assert.Equal(bytes.LongLength, observations[^1].BytesTransferred);
        Assert.All(
            observations.Zip(observations.Skip(1)),
            pair => Assert.True(pair.First.BytesTransferred < pair.Second.BytesTransferred));
    }

    [Fact]
    public async Task Copy_does_not_dispose_caller_owned_streams()
    {
        var source = new DisposeTrackingStream([1, 2, 3]);
        var destination = new DisposeTrackingStream();

        await BoundedStreamCopier.CopyAsync(
            source,
            destination,
            expectedLength: 3,
            cancellationToken: CancellationToken.None);

        Assert.False(source.WasDisposed);
        Assert.False(destination.WasDisposed);

        await source.DisposeAsync();
        await destination.DisposeAsync();
    }

    [Fact]
    public async Task Copy_fails_when_source_ends_before_expected_length()
    {
        await using var source = new MemoryStream([1, 2]);
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<EndOfStreamException>(() =>
            BoundedStreamCopier.CopyAsync(
                source,
                destination,
                expectedLength: 3,
                cancellationToken: CancellationToken.None));

        Assert.Contains("3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_fails_when_source_contains_bytes_beyond_approved_length()
    {
        await using var source = new MemoryStream([1, 2, 3, 4]);
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<SourceLengthExceededException>(() =>
            BoundedStreamCopier.CopyAsync(
                source,
                destination,
                expectedLength: 3,
                cancellationToken: CancellationToken.None));

        Assert.Equal(3, exception.ExpectedLength);
        Assert.Equal([1, 2, 3], destination.ToArray());
    }

    [Fact]
    public async Task Copy_honors_pre_cancelled_token()
    {
        await using var source = new MemoryStream([1, 2, 3]);
        await using var destination = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedStreamCopier.CopyAsync(
                source,
                destination,
                expectedLength: 3,
                cancellationToken: cancellation.Token));
        Assert.Empty(destination.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(BoundedStreamCopier.MaximumBufferSize + 1)]
    public async Task Copy_rejects_unbounded_or_invalid_buffer_sizes(int bufferSize)
    {
        await using var source = new MemoryStream();
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BoundedStreamCopier.CopyAsync(
                source,
                destination,
                expectedLength: 0,
                bufferSize: bufferSize,
                cancellationToken: CancellationToken.None));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ReadSizeTrackingStream(byte[] content) : MemoryStream(content)
    {
        public int LargestRequestedRead { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            LargestRequestedRead = Math.Max(LargestRequestedRead, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class DisposeTrackingStream : MemoryStream
    {
        public DisposeTrackingStream()
        {
        }

        public DisposeTrackingStream(byte[] content)
            : base(content, writable: false)
        {
        }

        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
