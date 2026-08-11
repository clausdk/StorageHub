using System.Reflection;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ConnectionSidebarControlTests
{
    [Fact]
    public void SidebarBuildsNestedSearchableGroupsAndRaisesConnectionSelection()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var productionId = Guid.NewGuid();
            var shellId = Guid.NewGuid();
            ConnectionCardModel[] cards =
            [
                Card(productionId, "Production S3", StorageProviderKind.S3, "FRAGHUNT/Production"),
                Card(Guid.NewGuid(), "Loose SFTP", StorageProviderKind.Sftp, null),
                Card(shellId, "Admin shell", StorageProviderKind.Ssh, "Servers/Linux")
            ];
            using var sidebar = new ConnectionSidebarControl { Size = new Size(320, 700) };
            sidebar.CreateControl();
            ConnectionCardModel? selected = null;
            sidebar.ConnectionSelected += (_, card) => selected = card;

            sidebar.SetConnections(cards, searchText: null, selectedConnectionId: null);

            Assert.Equal(
                ["Storage", "Remote clients"],
                Descendants<ConnectionSidebarSectionHeader>(sidebar).Select(static header => header.Text));
            Assert.Contains(Descendants<ConnectionSidebarGroup>(sidebar), static group => group.Name == "storage/FRAGHUNT");
            Assert.Contains(Descendants<ConnectionSidebarGroup>(sidebar), static group => group.Name == "storage/FRAGHUNT/Production");
            Assert.Contains(Descendants<ConnectionSidebarGroup>(sidebar), static group => group.Name == "storage/Unsorted");
            var shell = Assert.Single(Descendants<ConnectionSidebarItem>(sidebar), item => item.Connection.ConnectionId == shellId);

            typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shell, [EventArgs.Empty]);

            Assert.Equal(shellId, sidebar.SelectedConnectionId);
            Assert.Equal(shellId, selected?.ConnectionId);

            sidebar.SetConnections(cards, "production", productionId);
            var result = Assert.Single(Descendants<ConnectionSidebarItem>(sidebar));
            Assert.Equal(productionId, result.Connection.ConnectionId);
            Assert.True(result.Selected);
        });
    }

    private static ConnectionCardModel Card(
        Guid id,
        string name,
        StorageProviderKind provider,
        string? folder) => new(
        name,
        provider,
        $"{provider} endpoint",
        "Saved",
        ConnectionId: id,
        FolderPath: folder);

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
