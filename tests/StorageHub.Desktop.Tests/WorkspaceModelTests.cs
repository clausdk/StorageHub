namespace StorageHub.Desktop.Tests;

public sealed class WorkspaceModelTests
{
    [Theory]
    [InlineData(1, WorkspaceLayout.SideBySide)]
    [InlineData(2, WorkspaceLayout.SideBySide)]
    [InlineData(2, WorkspaceLayout.TopAndBottom)]
    [InlineData(3, WorkspaceLayout.SideBySide)]
    [InlineData(3, WorkspaceLayout.TopAndBottom)]
    [InlineData(4, WorkspaceLayout.SideBySide)]
    public void PresetsContainStableUniquePaneIds(int count, WorkspaceLayout layout)
    {
        var model = WorkspaceLayoutModel.CreatePreset(count, layout);

        Assert.Equal(count, model.PaneCount);
        Assert.Equal(count, model.PaneIds.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, model.PaneIds);
        if (count > 1)
        {
            var root = Assert.IsType<WorkspaceSplitNode>(model.Root);
            Assert.Equal(layout == WorkspaceLayout.SideBySide
                ? WorkspaceSplitOrientation.Vertical
                : WorkspaceSplitOrientation.Horizontal, root.Orientation);
        }
    }

    [Fact]
    public void SplitCapsAtFourAndCloseCollapsesTheEmptyParent()
    {
        var model = WorkspaceLayoutModel.CreatePreset(1, WorkspaceLayout.SideBySide);
        var original = model.PaneIds[0];
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var fourth = Guid.NewGuid();

        Assert.True(model.Split(original, WorkspaceDockEdge.Right, second));
        Assert.True(model.Split(second, WorkspaceDockEdge.Bottom, third));
        Assert.True(model.Split(third, WorkspaceDockEdge.Left, fourth));
        Assert.False(model.Split(fourth, WorkspaceDockEdge.Right, Guid.NewGuid()));
        Assert.True(model.Close(third));
        Assert.Equal([original, second, fourth], model.PaneIds);
        Assert.False(model.Close(Guid.NewGuid()));
    }

    [Fact]
    public void SwapAndEdgeMoveRetainStableIdsAndChangeTraversalOrder()
    {
        var model = WorkspaceLayoutModel.CreatePreset(3, WorkspaceLayout.SideBySide);
        var original = model.PaneIds.ToArray();

        Assert.True(model.Swap(original[0], original[2]));
        Assert.Equal([original[2], original[1], original[0]], model.PaneIds);
        Assert.True(model.MoveBeside(original[0], original[2], WorkspaceDockEdge.Left));
        Assert.Equal(original.Order(), model.PaneIds.Order());
        Assert.Equal(original[0], model.PaneIds[0]);
    }

    [Fact]
    public void RatiosAreNormalizedToSupportedBounds()
    {
        var model = WorkspaceLayoutModel.CreatePreset(2, WorkspaceLayout.SideBySide);
        var split = Assert.IsType<WorkspaceSplitNode>(model.Root);

        Assert.True(model.SetRatio(split, .01));
        Assert.Equal(WorkspaceLayoutModel.MinimumRatio, Assert.IsType<WorkspaceSplitNode>(model.Root).Ratio);
    }

    [Fact]
    public void WorkspaceFileRoundTripsWithoutRuntimeOrSecurityState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storagehub-{Guid.NewGuid():N}.shw");
        try
        {
            var model = WorkspaceLayoutModel.CreatePreset(2, WorkspaceLayout.SideBySide);
            var panes = new Dictionary<Guid, BrowserPaneState>
            {
                [model.PaneIds[0]] = new(PaneContentKind.ThisPc, FolderPath: @"C:\Data", Filter: "*.txt", SortColumn: BrowserSortColumn.Modified, SortAscending: false),
                [model.PaneIds[1]] = new(PaneContentKind.SavedStorage, Guid.NewGuid(), "Archive", "reports")
            };
            var document = WorkspaceFileStore.Capture("Research", model.PaneIds[1], model.Root, panes);

            WorkspaceFileStore.Save(path, document);
            var restored = WorkspaceFileStore.Load(path);
            var json = File.ReadAllText(path);

            Assert.Equal("Research", restored.Name);
            Assert.Equal(model.PaneIds[1], restored.ActivePaneId);
            Assert.Equal("*.txt", restored.Panes[model.PaneIds[0]].Filter);
            Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("terminalBuffer", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("clipboard", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transferJob", json, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnsupportedWorkspaceSchemaIsRejectedBeforeHydration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storagehub-{Guid.NewGuid():N}.shw");
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":999,\"name\":\"Future\",\"activePaneId\":\"00000000-0000-0000-0000-000000000001\",\"layout\":{\"kind\":\"leaf\",\"paneId\":\"00000000-0000-0000-0000-000000000001\"},\"panes\":{}}");
            var error = Assert.Throws<InvalidDataException>(() => WorkspaceFileStore.Load(path));
            Assert.Contains("999", error.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }
}
