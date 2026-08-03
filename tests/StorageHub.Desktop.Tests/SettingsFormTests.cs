using System.Reflection;

namespace StorageHub.Desktop.Tests;

public sealed class SettingsFormTests
{
    [Fact]
    public void Settings_cards_keep_readable_width()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var settings = new SettingsForm();
            settings.CreateControl();

            var pages = GetField<Dictionary<string, Control>>(settings, "_pages");
            var cards = pages.Values
                .SelectMany(DescendantsAndSelf)
                .OfType<TableLayoutPanel>()
                .Where(control => control.Name == "InformationCard")
                .ToArray();

            Assert.Single(cards);
            Assert.All(cards, card =>
            {
                Assert.True(card.AutoSize);
                Assert.Equal(AutoSizeMode.GrowAndShrink, card.AutoSizeMode);
                Assert.True(card.MinimumSize.Width >= 650);
                Assert.Equal(2, card.RowCount);
                Assert.All(card.Controls.Cast<Control>(), child =>
                    Assert.Equal(DockStyle.Fill, child.Dock));
            });
        });
    }

    [Fact]
    public void Navigation_is_a_tree_and_about_remains_in_the_help_menu()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var settings = new SettingsForm();
            var navigation = GetField<TreeView>(settings, "_categories");

            Assert.Contains(navigation.Nodes.Cast<TreeNode>(), node => node.Text == "Transfers & sync");
            Assert.DoesNotContain(navigation.Nodes.Cast<TreeNode>(), node => node.Text == "General");
            Assert.Contains(
                navigation.Nodes.Cast<TreeNode>().SelectMany(node => node.Nodes.Cast<TreeNode>()),
                node => node.Text == "Concurrency");
            Assert.DoesNotContain(navigation.Nodes.Cast<TreeNode>(), node => node.Text.Contains("About", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ApplyPersistsConnectionAndUpdateChoicesTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storagehub-settings-form-{Guid.NewGuid():N}");
        try
        {
            var store = new DesktopUpdatePreferencesStore(Path.Combine(root, "settings.json"));
            SyncRunReviewControlTests.RunOnSta(() =>
            {
                using var settings = new SettingsForm(store, saved: null);
                GetField<CheckBox>(settings, "_checkAutomatically").Checked = false;
                GetField<NumericUpDown>(settings, "_maximumTransferConcurrency").Value = 9;
                GetField<NumericUpDown>(settings, "_perConnectionConcurrency").Value = 3;
                GetField<NumericUpDown>(settings, "_maximumSyncConcurrency").Value = 4;
                var discovery = GetField<ComboBox>(settings, "_sshDiscovery");
                discovery.SelectedIndex = discovery.Items
                    .Cast<object>()
                    .Select((choice, index) => (choice, index))
                    .Single(item => item.choice.ToString()!.Contains(
                        "automatically",
                        StringComparison.OrdinalIgnoreCase))
                    .index;

                Assert.True(InvokeTrySave(settings));
            });

            var saved = store.Load();
            Assert.False(saved.CheckAutomatically);
            Assert.Equal(SshHostKeyDiscoveryMode.Automatic, saved.SshHostKeyDiscovery);
            Assert.True(saved.AdaptiveConcurrency);
            Assert.Equal(9, saved.MaximumTransferConcurrency);
            Assert.Equal(3, saved.PerConnectionConcurrency);
            Assert.Equal(4, saved.MaximumSyncConcurrency);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool InvokeTrySave(SettingsForm settings)
    {
        var method = typeof(SettingsForm).GetMethod("TrySave", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method.Invoke(settings, null));
    }

    private static T GetField<T>(object instance, string name)
        where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private static IEnumerable<Control> DescendantsAndSelf(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (var descendant in DescendantsAndSelf(child))
            {
                yield return descendant;
            }
        }
    }
}
