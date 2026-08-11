namespace StorageHub.Desktop.Tests;

public sealed class ConnectionProfileTreeTests
{
    [Fact]
    public void BuildAssignsEveryConnectionToItsStorageOrClientType()
    {
        var favoriteId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var disabledId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        ConnectionCardModel[] connections =
        [
            Card(favoriteId, "Favorite", StorageProviderKind.S3, favorite: true, folder: "Operations"),
            Card(folderId, "Folder", StorageProviderKind.Sftp, folder: "Operations"),
            Card(providerId, "Provider", StorageProviderKind.Ftp),
            Card(disabledId, "Disabled favorite", StorageProviderKind.Ftps, favorite: true, enabled: false, folder: "Operations"),
            Card(clientId, "Shell", StorageProviderKind.Ssh, folder: "Operations")
        ];

        var sections = ConnectionProfileTree.Build(connections);

        Assert.Equal(
            3,
            sections.Count(static section => section.Kind == ConnectionProfileSectionKind.Storage));
        Assert.Single(sections, static section => section.Kind == ConnectionProfileSectionKind.Client);
        Assert.Single(sections, static section => section.Kind == ConnectionProfileSectionKind.Disabled);
        Assert.Contains(sections, static section => section.Kind == ConnectionProfileSectionKind.Storage && section.Label == "Operations");
        Assert.Contains(sections, static section => section.Kind == ConnectionProfileSectionKind.Client && section.Label == "Operations");
        Assert.Equal(
            connections.Select(static connection => connection.ConnectionId).Order(),
            sections.SelectMany(static section => section.Connections)
                .Select(static connection => connection.ConnectionId)
                .Order());
    }

    [Fact]
    public void BuildCoalescesFolderNamesIgnoringCaseAndSortsConnections()
    {
        ConnectionCardModel[] connections =
        [
            Card(Guid.NewGuid(), "Zulu", StorageProviderKind.S3, folder: "Team"),
            Card(Guid.NewGuid(), "alpha", StorageProviderKind.Sftp, folder: "team"),
            Card(Guid.NewGuid(), "Bravo", StorageProviderKind.Ftp, folder: " TEAM ")
        ];

        var section = Assert.Single(ConnectionProfileTree.Build(connections));

        Assert.Equal(ConnectionProfileSectionKind.Storage, section.Kind);
        Assert.Equal(["alpha", "Bravo", "Zulu"], section.Connections.Select(static connection => connection.Name));
    }

    [Theory]
    [InlineData("cold", "Tagged")]
    [InlineData("finance", "Foldered")]
    [InlineData("object storage", "Provider")]
    [InlineData("disabled", "Offline")]
    public void BuildSearchesTagsFoldersProvidersAndState(string query, string expectedName)
    {
        ConnectionCardModel[] connections =
        [
            Card(Guid.NewGuid(), "Tagged", StorageProviderKind.Ftp, tags: ["cold-storage"]),
            Card(Guid.NewGuid(), "Foldered", StorageProviderKind.Sftp, folder: "Finance"),
            Card(Guid.NewGuid(), "Provider", StorageProviderKind.S3),
            Card(Guid.NewGuid(), "Offline", StorageProviderKind.Local, enabled: false)
        ];

        var match = Assert.Single(ConnectionProfileTree.Build(connections, query).SelectMany(static section => section.Connections));

        Assert.Equal(expectedName, match.Name);
    }

    [Fact]
    public void BuildWithNoMatchesReturnsNoFakeConnections()
    {
        var sections = ConnectionProfileTree.Build(
            [Card(Guid.NewGuid(), "Archive", StorageProviderKind.S3, tags: ["production"])],
            "development");

        Assert.Empty(sections);
    }

    private static ConnectionCardModel Card(
        Guid id,
        string name,
        StorageProviderKind provider,
        bool favorite = false,
        bool enabled = true,
        string? folder = null,
        string[]? tags = null) => new(
            name,
            provider,
            $"{provider} endpoint",
            enabled ? "Saved" : "Disabled",
            favorite,
            id,
            enabled,
            AccentColor: null,
            folder,
            tags);
}
