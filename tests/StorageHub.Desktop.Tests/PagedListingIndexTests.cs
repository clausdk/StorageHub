using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class PagedListingIndexTests
{
    [Fact]
    public void DiskIndexPagesSortsAndFiltersThousandsWithoutMaterializingAView()
    {
        using var index = new PagedListingIndex();
        index.Reset(Enumerable.Range(0, 50_000).Select(number => new BrowserListItem(
            $"file-{number:D5}.txt",
            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "TXT file",
            string.Empty,
            string.Empty,
            $"folder/file-{number:D5}.txt",
            Kind: StorageItemKind.File,
            Length: number)));

        var descending = index.CreateView(BrowserSortColumn.Name, ascending: false);
        var filtered = index.CreateView(BrowserSortColumn.Name, ascending: true, "file-499??.txt");

        Assert.Equal(50_000, descending.Count);
        Assert.Equal("file-49999.txt", descending[0].Name);
        Assert.Equal("file-00000.txt", descending[^1].Name);
        Assert.Equal(100, filtered.Count);
        Assert.Equal("file-49900.txt", filtered[0].Name);
        Assert.Equal("file-49999.txt", filtered[^1].Name);
        var conflicts = index.FindByNames(["file-00001.txt", "file-49999.txt", "missing.txt"]);
        Assert.Equal(
            ["file-00001.txt", "file-49999.txt"],
            conflicts.Select(static item => item.Name).Order(StringComparer.Ordinal));

        for (var indexNumber = 0; indexNumber < 30; indexNumber++)
        {
            _ = descending[indexNumber * 256];
        }
        Assert.InRange(index.CachedPageCount, 1, 16);
    }

    [Fact]
    public void FindIndexesResolvesEveryRequestedLocationInOneRankingPass()
    {
        using var index = new PagedListingIndex();
        index.Reset(Enumerable.Range(0, 1_000).Select(number => new BrowserListItem(
            $"file-{number:D4}.txt",
            number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "TXT file",
            string.Empty,
            string.Empty,
            $"folder/file-{number:D4}.txt",
            Kind: StorageItemKind.File,
            Length: number)));

        var ascending = index.FindIndexes(
            BrowserSortColumn.Name,
            ascending: true,
            filter: null,
            ["folder/file-0000.txt", "folder/file-0999.txt", "folder/missing.txt"]);

        Assert.Equal(0, ascending["folder/file-0000.txt"]);
        Assert.Equal(999, ascending["folder/file-0999.txt"]);
        Assert.False(ascending.ContainsKey("folder/missing.txt"));

        var descending = index.FindIndexes(
            BrowserSortColumn.Name,
            ascending: false,
            filter: null,
            ["folder/file-0999.txt"]);

        Assert.Equal(0, descending["folder/file-0999.txt"]);

        // A filtered ranking numbers rows within the filtered set, not the whole listing.
        var filtered = index.FindIndexes(
            BrowserSortColumn.Name,
            ascending: true,
            "file-099?.txt",
            ["folder/file-0990.txt", "folder/file-0000.txt"]);

        Assert.Equal(0, filtered["folder/file-0990.txt"]);
        Assert.False(filtered.ContainsKey("folder/file-0000.txt"));

        Assert.Empty(index.FindIndexes(BrowserSortColumn.Name, ascending: true, filter: null, []));
    }
}
