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
}
