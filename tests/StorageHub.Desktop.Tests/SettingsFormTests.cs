using System.Reflection;

namespace StorageHub.Desktop.Tests;

public sealed class SettingsFormTests
{
    [Fact]
    public void GeneralInformationCardsKeepReadableWidth()
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

            Assert.Equal(3, cards.Length);
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
