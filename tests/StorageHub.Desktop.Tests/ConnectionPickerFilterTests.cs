using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ConnectionPickerFilterTests
{
    [Fact]
    public void AnEmptyQueryKeepsEveryConnection()
    {
        Assert.True(ConnectionPickerFilter.Matches(Card("Archive"), null));
        Assert.True(ConnectionPickerFilter.Matches(Card("Archive"), ""));
        Assert.True(ConnectionPickerFilter.Matches(Card("Archive"), "   "));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndPartial()
    {
        var card = Card("Production Archive");

        Assert.True(ConnectionPickerFilter.Matches(card, "prod"));
        Assert.True(ConnectionPickerFilter.Matches(card, "ARCHIVE"));
        Assert.False(ConnectionPickerFilter.Matches(card, "staging"));
    }

    [Fact]
    public void TheSameFieldsAsConnectionManagerAreSearched()
    {
        var card = new ConnectionCardModel(
            "Archive",
            StorageProviderKind.S3,
            "s3.example.test",
            "Healthy",
            ConnectionId: Guid.NewGuid(),
            FolderPath: "Partners/Acme",
            Tags: ["nightly"]);

        Assert.True(ConnectionPickerFilter.Matches(card, "s3.example"));
        Assert.True(ConnectionPickerFilter.Matches(card, "Acme"));
        Assert.True(ConnectionPickerFilter.Matches(card, "healthy"));
        Assert.True(ConnectionPickerFilter.Matches(card, "nightly"));
        Assert.True(ConnectionPickerFilter.Matches(card, "S3"));
    }

    [Fact]
    public void EveryTermMustMatchSoTypingMoreNarrows()
    {
        var archive = new ConnectionCardModel(
            "Archive", StorageProviderKind.S3, "s3.example.test", "Healthy", ConnectionId: Guid.NewGuid());
        var backups = new ConnectionCardModel(
            "Backups", StorageProviderKind.S3, "s3.example.test", "Healthy", ConnectionId: Guid.NewGuid());

        Assert.True(ConnectionPickerFilter.Matches(archive, "s3 archive"));
        Assert.False(ConnectionPickerFilter.Matches(backups, "s3 archive"));
    }

    [Fact]
    public void RowsCarryAHeadingWheneverTheGroupChanges()
    {
        var cards = new[] { Card("A", "Favourites"), Card("B", "Favourites"), Card("C", "Storage") };

        var rows = ConnectionPickerFilter.BuildRows(cards, null, GroupOf);

        Assert.Equal(5, rows.Count);
        Assert.True(rows[0].IsHeader);
        Assert.Equal("Favourites", rows[0].GroupLabel);
        Assert.False(rows[1].IsHeader);
        Assert.False(rows[2].IsHeader);
        Assert.True(rows[3].IsHeader);
        Assert.Equal("Storage", rows[3].GroupLabel);
    }

    [Fact]
    public void FilteringDropsHeadingsThatNoLongerHaveMembers()
    {
        // A heading for a group whose only connection was filtered out would be a lie.
        var cards = new[] { Card("Archive", "Favourites"), Card("Backups", "Storage") };

        var rows = ConnectionPickerFilter.BuildRows(cards, "archive", GroupOf);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Favourites", rows[0].GroupLabel);
        Assert.Equal("Archive", rows[1].Card!.Name);
    }

    [Fact]
    public void AQueryThatMatchesNothingProducesNoRows()
    {
        var rows = ConnectionPickerFilter.BuildRows([Card("Archive")], "zzz", GroupOf);

        Assert.Empty(rows);
    }

    [Fact]
    public void NavigationSkipsHeadings()
    {
        var rows = ConnectionPickerFilter.BuildRows(
            [Card("A", "Favourites"), Card("B", "Storage")], null, GroupOf);

        // Rows: header, A, header, B
        Assert.Equal(1, ConnectionPickerFilter.NextSelectable(rows, 0, 1));
        Assert.Equal(3, ConnectionPickerFilter.NextSelectable(rows, 2, 1));
        Assert.Equal(1, ConnectionPickerFilter.NextSelectable(rows, 2, -1));
        Assert.Equal(-1, ConnectionPickerFilter.NextSelectable(rows, 4, 1));
    }

    private static string GroupOf(ConnectionCardModel card) => card.Endpoint;

    private static ConnectionCardModel Card(string name, string group = "Storage") =>
        new(name, StorageProviderKind.S3, group, "Healthy", ConnectionId: Guid.NewGuid());
}
