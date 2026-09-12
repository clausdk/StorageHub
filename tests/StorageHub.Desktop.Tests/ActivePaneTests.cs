namespace StorageHub.Desktop.Tests;

public sealed class ActivePaneTests
{
    [Theory]
    [InlineData(DesktopAppearance.Light)]
    [InlineData(DesktopAppearance.Dark)]
    public void OnlyActivePaneHasAccentBorderAndMarkerAfterSwitchAndSwap(DesktopAppearance appearance)
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var previous = DesktopAppearanceService.Appearance;
            try
            {
                DesktopAppearanceService.SetAppearance(appearance);
                using var main = new MainForm();
                var page = main.AddWorkspace(2);
                var workspace = Assert.Single(page.Controls.OfType<WorkspaceControl>());
                workspace.Size = new Size(1000, 600);
                workspace.PerformLayout();
                var ids = workspace.LayoutModel.PaneIds.ToArray();
                Verify(workspace, ids[0]);
                workspace.ActivatePane(ids[1]);
                Verify(workspace, ids[1]);
                Assert.True(workspace.SwapPanes(ids[0], ids[1]));
                Verify(workspace, ids[1]);
                Assert.True(workspace.ClosePane(ids[1]));
                Verify(workspace, ids[0]);
            }
            finally { DesktopAppearanceService.SetAppearance(previous); }
        });
    }

    [Fact]
    public void ClosingAPaneLeavesEveryHeaderNumberedFromItsRealPosition()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var main = new MainForm();
            var page = main.AddWorkspace(4);
            var workspace = Assert.Single(page.Controls.OfType<WorkspaceControl>());
            workspace.Size = new Size(1200, 800);
            main.CreateControl();
            main.PerformLayout();
            workspace.PerformLayout();

            var victim = workspace.LayoutModel.PaneIds[1];
            Assert.True(workspace.ClosePane(victim));

            Assert.Equal(3, workspace.LayoutModel.PaneCount);
            Assert.Equal(3, workspace.Panes.Count);
            Assert.DoesNotContain(victim, workspace.LayoutModel.PaneIds);
            Assert.NotEqual(victim, workspace.ActivePaneId);

            // A header left over from the closed pane numbered itself from a position of -1 and
            // showed as "Pane 0", which is what made the numbering skip to "Pane 2" afterwards.
            var labels = workspace.Panes
                .Select(candidate => Assert.IsType<Panel>(candidate.Parent))
                .Select(frame => Assert.Single(frame.Controls.OfType<ToolStrip>()))
                .Select(header => header.Items["PaneTitle"]!.Text!.Replace(" (Active)", string.Empty, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["Pane 1", "Pane 2", "Pane 3"], labels);
        });
    }

    private static void Verify(WorkspaceControl workspace, Guid active)
    {
        Assert.Equal(active, workspace.ActivePaneId);
        foreach (var pane in workspace.Panes)
        {
            var frame = Assert.IsType<Panel>(pane.Parent);
            var id = Assert.IsType<Guid>(frame.Tag);
            Assert.True(frame.Padding.Left >= 2);
            var header = Assert.Single(frame.Controls.OfType<ToolStrip>());
            Assert.Equal(id == active, header.Items["PaneTitle"]!.Text!.Contains("(Active)", StringComparison.Ordinal));
            frame.Size = new Size(460, 300);
            using var bitmap = new Bitmap(frame.Width, frame.Height);
            frame.DrawToBitmap(bitmap, frame.ClientRectangle);
            Assert.Equal((id == active ? StorageHubTheme.Primary : StorageHubTheme.Border).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        }
    }
}
