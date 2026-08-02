using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class RemoteStorageBrowserTests
{
    [Theory]
    [InlineData("../private")]
    [InlineData("folder/%2e%2e/private")]
    [InlineData("C:\\private")]
    [InlineData("\\\\server\\share")]
    public void RemotePathsRejectTraversalAndAbsoluteLocations(string value)
    {
        Assert.False(RemoteBrowserPath.TryNormalize(value, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task SelectionTestsBeforeOpeningAndNavigationCommitsHistoryOnlyAfterSuccess()
    {
        var connection = CreateConnection("Archive", favorite: true);
        var calls = new List<string>();
        var client = new FakeRemoteClient
        {
            ListConnections = (_, _) => Task.FromResult(new ConnectionListResponse(
                StorageIpcContract.CurrentVersion,
                [connection])),
            TestConnection = (request, _) =>
            {
                calls.Add("test:" + request.ConnectionId);
                return Task.FromResult(new ConnectionTestResponse(
                    StorageIpcContract.CurrentVersion,
                    request.ConnectionId,
                    Succeeded: true,
                    ElapsedMilliseconds: 3));
            },
            ListStorage = (request, _) =>
            {
                calls.Add("list:" + request.RelativePath);
                StorageListItem[] entries = request.RelativePath.Length == 0
                    ? [Item("folder", "folder", isContainer: true)]
                    : [Item("file.txt", "folder/file.txt")];
                return Task.FromResult(new StorageListPageResponse(
                    StorageIpcContract.CurrentVersion,
                    request.ConnectionId,
                    request.RelativePath,
                    entries,
                    ContinuationToken: null,
                    RootIdentity: "root-browser"));
            }
        };
        await using var controller = new RemoteBrowserController(client);

        var loaded = await controller.LoadConnectionsAsync();
        var selected = await controller.SelectConnectionAsync(connection.ConnectionId);
        var opened = await controller.NavigateAsync(RemoteBrowserNavigationKind.Navigate, "/folder/");

        Assert.Equal(RemoteBrowserOperationStatus.Succeeded, loaded.Status);
        Assert.Equal(RemoteBrowserOperationStatus.Succeeded, selected.Status);
        Assert.Equal(RemoteBrowserOperationStatus.Succeeded, opened.Status);
        Assert.Equal("folder", opened.Snapshot?.RelativePath);
        Assert.Equal("root-browser", opened.Snapshot?.RootIdentity);
        Assert.True(controller.CanGoBack);

        var backed = await controller.NavigateAsync(RemoteBrowserNavigationKind.Back);

        Assert.Equal(RemoteBrowserOperationStatus.Succeeded, backed.Status);
        Assert.Equal(string.Empty, backed.Snapshot?.RelativePath);
        Assert.Equal(
            [
                "test:" + connection.ConnectionId,
                "list:",
                "list:folder",
                "list:"
            ],
            calls);
    }

    [Fact]
    public async Task LoadMoreAppendsOneBoundedPageAndRejectsRepeatingContinuation()
    {
        var connection = CreateConnection("Paged");
        var page = 0;
        var client = CreateSelectableClient(connection, (request, _) =>
        {
            page++;
            return Task.FromResult(page switch
            {
                1 => Response(request, [Item("one.txt", "one.txt")], "next-1"),
                2 => Response(request, [Item("two.txt", "two.txt")], "next-2"),
                _ => Response(request, [Item("three.txt", "three.txt")], "next-2")
            });
        });
        await using var controller = new RemoteBrowserController(client);
        _ = await controller.LoadConnectionsAsync();
        var selected = await controller.SelectConnectionAsync(connection.ConnectionId);

        var more = await controller.LoadMoreAsync();
        var inconsistent = await controller.LoadMoreAsync();

        Assert.Equal(RemoteBrowserOperationStatus.Succeeded, selected.Status);
        Assert.Equal(RemoteBrowserOperationStatus.Succeeded, more.Status);
        Assert.Equal(2, more.Snapshot?.Entries.Count);
        Assert.Equal(RemoteBrowserOperationStatus.Failed, inconsistent.Status);
        Assert.Contains("inconsistent", inconsistent.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, controller.CurrentSnapshot!.Entries.Count);
    }

    [Fact]
    public async Task NewNavigationSupersedesOlderListingAndDoesNotCommitItsPath()
    {
        var connection = CreateConnection("Remote");
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateSelectableClient(connection, async (request, token) =>
        {
            if (request.RelativePath == "slow")
            {
                slowStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return Response(request, [], null);
        });
        await using var controller = new RemoteBrowserController(client);
        _ = await controller.LoadConnectionsAsync();
        _ = await controller.SelectConnectionAsync(connection.ConnectionId);

        var slow = controller.NavigateAsync(RemoteBrowserNavigationKind.Navigate, "slow");
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var fast = await controller.NavigateAsync(RemoteBrowserNavigationKind.Navigate, "fast");
        var superseded = await slow.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RemoteBrowserOperationStatus.Succeeded, fast.Status);
        Assert.Equal("fast", controller.CurrentSnapshot?.RelativePath);
        Assert.Equal(RemoteBrowserOperationStatus.Superseded, superseded.Status);
    }

    [Fact]
    public async Task ControllerDoesNotExposeTransportExceptionDetails()
    {
        var client = new FakeRemoteClient
        {
            ListConnections = (_, _) => Task.FromException<ConnectionListResponse>(
                new IOException("pipe included password=hunter2"))
        };
        await using var controller = new RemoteBrowserController(client);

        var result = await controller.LoadConnectionsAsync();

        Assert.Equal(RemoteBrowserOperationStatus.Failed, result.Status);
        Assert.Equal("The background agent is unavailable.", result.ErrorMessage);
        Assert.DoesNotContain("hunter2", result.ErrorMessage, StringComparison.Ordinal);
    }

    private static FakeRemoteClient CreateSelectableClient(
        ConnectionSummary connection,
        Func<StorageListPageRequest, CancellationToken, Task<StorageListPageResponse>> list) => new()
        {
            ListConnections = (_, _) => Task.FromResult(new ConnectionListResponse(
                StorageIpcContract.CurrentVersion,
                [connection])),
            TestConnection = (request, _) => Task.FromResult(new ConnectionTestResponse(
                StorageIpcContract.CurrentVersion,
                request.ConnectionId,
                Succeeded: true,
                ElapsedMilliseconds: 1)),
            ListStorage = list
        };

    private static StorageListPageResponse Response(
        StorageListPageRequest request,
        StorageListItem[] items,
        string? token) => new(
        StorageIpcContract.CurrentVersion,
        request.ConnectionId,
        request.RelativePath,
        items,
        token,
        RootIdentity: "root-browser");

    private static ConnectionSummary CreateConnection(string name, bool favorite = false) => new(
        Guid.NewGuid(),
        name,
        StorageConnectionProvider.S3,
        FolderPath: null,
        Tags: [],
        IsFavorite: favorite,
        IsEnabled: true,
        IconKey: "cloud",
        AccentColor: "#3366CC",
        Version: 1);

    private static StorageListItem Item(
        string name,
        string path,
        bool isContainer = false) => new(
        name,
        path,
        isContainer ? StorageItemKind.Directory : StorageItemKind.File,
        isContainer ? null : 42,
        LastModifiedUtc: null,
        ContentType: null,
        isContainer);

    private sealed class FakeRemoteClient : IRemoteStorageAgentClient
    {
        public Func<ConnectionListRequest, CancellationToken, Task<ConnectionListResponse>> ListConnections { get; init; } =
            static (_, _) => throw new NotSupportedException();

        public Func<ConnectionTestRequest, CancellationToken, Task<ConnectionTestResponse>> TestConnection { get; init; } =
            static (_, _) => throw new NotSupportedException();

        public Func<StorageListPageRequest, CancellationToken, Task<StorageListPageResponse>> ListStorage { get; init; } =
            static (_, _) => throw new NotSupportedException();

        public Task<ConnectionListResponse> ListConnectionsAsync(
            ConnectionListRequest request,
            CancellationToken cancellationToken = default) => ListConnections(request, cancellationToken);

        public Task<ConnectionTestResponse> TestConnectionAsync(
            ConnectionTestRequest request,
            CancellationToken cancellationToken = default) => TestConnection(request, cancellationToken);

        public Task<StorageListPageResponse> ListStorageAsync(
            StorageListPageRequest request,
            CancellationToken cancellationToken = default) => ListStorage(request, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
