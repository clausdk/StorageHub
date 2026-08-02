namespace StorageHub.Desktop.Tests;

public sealed class UiCommandCatalogTests
{
    [Fact]
    public void TopMenusMatchTheProductNavigationContract()
    {
        Assert.Equal(
            ["File", "Edit", "View", "Go", "Connections", "Transfer", "Sync", "Tools", "Help"],
            UiCommandCatalog.TopMenus);
    }

    [Theory]
    [InlineData("Connections", "Connection Manager...")]
    [InlineData("Transfer", "Pause All")]
    [InlineData("Sync", "Preview Sync...")]
    [InlineData("Tools", "Diagnostics...")]
    public void CriticalCommandsAreReachableFromTopMenus(string menu, string command)
    {
        Assert.Contains(command, UiCommandCatalog.Commands[menu]);
    }

    [Fact]
    public void EveryMenuCommandHasAUniquePresentationDefinition()
    {
        var expectedCount = UiCommandCatalog.Commands.Sum(pair => pair.Value.Count);

        Assert.Equal(expectedCount, UiCommandCatalog.Definitions.Count);
        Assert.Equal(expectedCount, UiCommandCatalog.Definitions.Select(definition => definition.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(UiCommandCatalog.Definitions, definition => Assert.False(string.IsNullOrWhiteSpace(definition.Description)));
    }

    [Theory]
    [InlineData("File", "New Workspace Tab", Keys.Control | Keys.T)]
    [InlineData("View", "Refresh", Keys.F5)]
    [InlineData("Connections", "Quick Connect...", Keys.Control | Keys.K)]
    [InlineData("Sync", "Compare Panes", Keys.Control | Keys.D)]
    public void CriticalCommandsExposeKeyboardShortcuts(string menu, string command, Keys shortcut)
    {
        Assert.Equal(shortcut, UiCommandCatalog.GetDefinition(menu, command).Shortcut);
    }
}
