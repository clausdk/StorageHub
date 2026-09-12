using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// Go ▸ Favorites lists connections the active pane can actually open. The selection is a pure
/// static so that rule is testable without a window or a pipe.
/// </summary>
public sealed class FavoriteConnectionMenuTests
{
    [Fact]
    public void OnlyFavoritesAreOffered()
    {
        var selected = OverviewDashboardControl.SelectFavoriteConnections(
            [Connection("Kept"), Connection("Skipped", isFavorite: false)]);

        Assert.Equal("Kept", Assert.Single(selected).DisplayName);
    }

    [Fact]
    public void ADisabledFavoriteIsNotOffered()
    {
        // A disabled profile cannot be opened, so listing it would be a dead menu entry.
        Assert.Empty(OverviewDashboardControl.SelectFavoriteConnections(
            [Connection("Retired", isEnabled: false)]));
    }

    [Fact]
    public void ClientConnectionsAreOfferedOnlyWhenAPaneCanOpenThem()
    {
        var selected = OverviewDashboardControl.SelectFavoriteConnections(
            [
                Connection("Shell", type: ConnectionProfileType.Client, provider: StorageConnectionProvider.Ssh),
                Connection("Bucket client", type: ConnectionProfileType.Client, provider: StorageConnectionProvider.S3)
            ]);

        Assert.Equal("Shell", Assert.Single(selected).DisplayName);
    }

    [Fact]
    public void StorageConnectionsAreOfferedWhateverTheirProvider()
    {
        var selected = OverviewDashboardControl.SelectFavoriteConnections(
            [
                Connection("Bucket", provider: StorageConnectionProvider.S3),
                Connection("Drop", provider: StorageConnectionProvider.Ftp)
            ]);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void FavoritesAreListedByNameAndCapped()
    {
        var selected = OverviewDashboardControl.SelectFavoriteConnections(
            [Connection("zulu"), Connection("Alpha"), Connection("mike")]);

        Assert.Equal(["Alpha", "mike", "zulu"], selected.Select(connection => connection.DisplayName));
        Assert.Equal(
            2,
            OverviewDashboardControl.SelectFavoriteConnections(
                [Connection("a"), Connection("b"), Connection("c")], maximum: 2).Count);
    }

    [Fact]
    public void AnEmptyOrMissingCacheYieldsNothingRatherThanThrowing()
    {
        // The menu can open before the overview's first refresh has filled its cache.
        Assert.Empty(OverviewDashboardControl.SelectFavoriteConnections(null));
        Assert.Empty(OverviewDashboardControl.SelectFavoriteConnections([]));
        Assert.Empty(OverviewDashboardControl.SelectFavoriteConnections([Connection("a")], maximum: 0));
    }

    private static ConnectionSummary Connection(
        string name,
        bool isFavorite = true,
        bool isEnabled = true,
        ConnectionProfileType type = ConnectionProfileType.Storage,
        StorageConnectionProvider provider = StorageConnectionProvider.Local) =>
        new(
            Guid.NewGuid(),
            name,
            provider,
            FolderPath: null,
            Tags: [],
            isFavorite,
            isEnabled,
            IconKey: null,
            AccentColor: null,
            Version: 1,
            type);
}
