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
