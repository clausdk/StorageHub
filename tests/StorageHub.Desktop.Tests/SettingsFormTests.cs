using System.Reflection;
using StorageHub.Contracts.Ipc;

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

            Assert.NotEmpty(cards);
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
            Assert.Contains(navigation.Nodes.Cast<TreeNode>(), node => node.Text == "Appearance");
            var connections = Assert.Single(navigation.Nodes.Cast<TreeNode>(), node => node.Text == "Connections");
            Assert.Contains(connections.Nodes.Cast<TreeNode>(), node => node.Text == "Storage");
            Assert.Contains(connections.Nodes.Cast<TreeNode>(), node => node.Text == "Clients");
            Assert.DoesNotContain(navigation.Nodes.Cast<TreeNode>(), node => node.Text == "General");
            Assert.Contains(
                navigation.Nodes.Cast<TreeNode>().SelectMany(node => node.Nodes.Cast<TreeNode>()),
                node => node.Text == "Concurrency");
            Assert.DoesNotContain(navigation.Nodes.Cast<TreeNode>(), node => node.Text.Contains("About", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void EveryCatalogProviderHasItsOwnSettingsPageUnderTheCorrectType()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            using var settings = new SettingsForm();
            var navigation = GetField<TreeView>(settings, "_categories");
            var pages = GetField<Dictionary<string, Control>>(settings, "_pages");
            var connections = Assert.Single(navigation.Nodes.Cast<TreeNode>(), node => node.Text == "Connections");

            foreach (var type in new[] { ConnectionProfileType.Storage, ConnectionProfileType.Client })
            {
                var typeLabel = type == ConnectionProfileType.Storage ? "Storage" : "Clients";
                var typeNode = Assert.Single(connections.Nodes.Cast<TreeNode>(), node => node.Text == typeLabel);
                var expected = ConnectionProviderCatalog.All.Where(provider => provider.Type == type).ToArray();
                Assert.Equal(expected.Select(provider => provider.DisplayName), typeNode.Nodes.Cast<TreeNode>().Select(node => node.Text));
                Assert.Contains($"ConnectionType:{type}", pages.Keys);
                foreach (var provider in expected)
                {
                    var page = Assert.IsType<FlowLayoutPanel>(pages[$"Provider:{provider.Kind}"]);
                    Assert.Contains(
                        page.Controls.Cast<Control>().SelectMany(DescendantsAndSelf),
                        control => control.Name == $"ProviderSettings:{provider.Kind}");
                    Assert.Contains(
                        page.Controls.OfType<Button>(),
                        button => button.AccessibleName == $"Configure {provider.DisplayName} connection");
                }
            }
        });
    }

    [Fact]
    public void Apply_persists_dark_appearance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storagehub-appearance-{Guid.NewGuid():N}");
        try
        {
            var store = new DesktopUpdatePreferencesStore(Path.Combine(root, "settings.json"));
            SyncRunReviewControlTests.RunOnSta(() =>
            {
                using var settings = new SettingsForm(store, saved: null);
                var appearance = GetField<ComboBox>(settings, "_appearance");
                appearance.SelectedItem = DesktopAppearance.Dark;
                Assert.Equal(DesktopAppearance.Dark, DesktopAppearanceService.EffectiveAppearance);
                Assert.True(InvokeTrySave(settings));
                Assert.Equal(DesktopAppearance.Dark, DesktopAppearanceService.EffectiveAppearance);
            });
            Assert.Equal(DesktopAppearance.Dark, store.Load().Appearance);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            SyncRunReviewControlTests.RunOnSta(
                () => DesktopAppearanceService.SetAppearance(DesktopAppearance.Light));
        }
    }

    [Fact]
    public void Appearance_supports_system_preview_and_cancel_rolls_back_to_last_applied_choice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storagehub-system-appearance-{Guid.NewGuid():N}");
        try
        {
            var store = new DesktopUpdatePreferencesStore(Path.Combine(root, "settings.json"));
            store.Save(DesktopUpdatePreferences.Defaults with { Appearance = DesktopAppearance.Light });
            SyncRunReviewControlTests.RunOnSta(() =>
            {
                DesktopAppearanceService.SetSystemDarkModeReaderForTests(() => true);
                DesktopAppearanceService.SetAppearance(DesktopAppearance.Light);
                using var settings = new SettingsForm(store, saved: null);
                settings.Show();
                var appearance = GetField<ComboBox>(settings, "_appearance");
                Assert.Equal(3, appearance.Items.Count);

                appearance.SelectedItem = DesktopAppearance.System;
                Assert.Equal(DesktopAppearance.System, DesktopAppearanceService.Appearance);
                Assert.Equal(DesktopAppearance.Dark, DesktopAppearanceService.EffectiveAppearance);

                settings.DialogResult = DialogResult.Cancel;
                settings.Close();
                Assert.Equal(DesktopAppearance.Light, DesktopAppearanceService.Appearance);
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            SyncRunReviewControlTests.RunOnSta(() =>
            {
                DesktopAppearanceService.SetSystemDarkModeReaderForTests(null);
                DesktopAppearanceService.SetAppearance(DesktopAppearance.System);
            });
        }
    }

    [Fact]
    public void Explicit_appearance_takes_precedence_over_system_changes()
    {
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var systemIsDark = false;
            DesktopAppearanceService.SetSystemDarkModeReaderForTests(() => systemIsDark);
            DesktopAppearanceService.SetAppearance(DesktopAppearance.Dark);
            systemIsDark = false;
            DesktopAppearanceService.RefreshSystemAppearance();
            Assert.Equal(DesktopAppearance.Dark, DesktopAppearanceService.EffectiveAppearance);
            DesktopAppearanceService.SetSystemDarkModeReaderForTests(null);
            DesktopAppearanceService.SetAppearance(DesktopAppearance.System);
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
                GetField<CheckBox>(settings, "_warnBeforeUnsafeExternalEdit").Checked = false;
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
            Assert.False(saved.WarnBeforeUnsafeExternalEdit);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ProviderPagesPersistGeneralDefaultsAndNewProfilesConsumeThem()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storagehub-provider-defaults-{Guid.NewGuid():N}");
        var sshKeyReference = "shs_" + new string('A', 43);
        try
        {
            var store = new DesktopUpdatePreferencesStore(Path.Combine(root, "settings.json"));
            SyncRunReviewControlTests.RunOnSta(() =>
            {
                using var settings = new SettingsForm(store, saved: null);
                var controls = GetField<Dictionary<string, Control>>(settings, "_connectionDefaultControls");
                Assert.DoesNotContain(controls.Keys, key =>
                    key.Contains("profileName", StringComparison.Ordinal) ||
                    key.Contains("username", StringComparison.Ordinal) ||
                    key.Contains("password", StringComparison.Ordinal) ||
                    key.Contains("fingerprint", StringComparison.OrdinalIgnoreCase));
                Assert.IsType<NumericUpDown>(controls["Ftp.port"]).Value = 2121;
                Assert.IsType<TextBox>(controls["Ftp.initialPath"]).Text = "/incoming";
                Assert.IsType<NumericUpDown>(controls["Ftp.operationTimeoutSeconds"]).Value = 90;
                Assert.IsType<NumericUpDown>(controls["Ftp.maximumRetryAttempts"]).Value = 4;
                Assert.IsType<TableLayoutPanel>(controls["Ssh.privateKeyReference"])
                    .Controls.OfType<TextBox>().Single().Text = sshKeyReference;

                Assert.True(InvokeTrySave(settings));
            });

            var saved = store.Load();
            var ftp = ConnectionDefaultSettings.Get(StorageProviderKind.Ftp, saved.ConnectionDefaults);
            Assert.Equal("2121", ftp.FieldValues["port"]);
            Assert.Equal("/incoming", ftp.FieldValues["initialPath"]);
            Assert.Equal(90, ftp.OperationTimeoutSeconds);
            Assert.Equal(4, ftp.MaximumRetryAttempts);
            var ssh = ConnectionDefaultSettings.Get(StorageProviderKind.Ssh, saved.ConnectionDefaults);
            Assert.Equal(sshKeyReference, ssh.FieldValues["privateKeyReference"]);
            SyncRunReviewControlTests.RunOnSta(() =>
            {
                using var manager = new ConnectionManagerForm(
                    initialProvider: StorageProviderKind.Ftp,
                    connectionDefaults: saved.ConnectionDefaults);
                var fields = GetField<Dictionary<string, Control>>(manager, "_editorFields");
                Assert.Equal(2121, Assert.IsType<NumericUpDown>(fields["port"]).Value);
                Assert.Equal("/incoming", Assert.IsType<TextBox>(fields["initialPath"]).Text);
                Assert.Empty(Assert.IsType<TextBox>(fields["host"]).Text);
                Assert.Empty(Assert.IsType<TextBox>(fields["username"]).Text);
            });
            SyncRunReviewControlTests.RunOnSta(() =>
            {
                using var manager = new ConnectionManagerForm(
                    initialProvider: StorageProviderKind.Ssh,
                    connectionDefaults: saved.ConnectionDefaults);
                var fields = GetField<Dictionary<string, Control>>(manager, "_editorFields");
                Assert.Equal(
                    sshKeyReference,
                    Assert.IsType<TableLayoutPanel>(fields["privateKeyReference"])
                        .Controls.OfType<TextBox>().Single().Text);
                Assert.Empty(
                    Assert.IsType<TableLayoutPanel>(fields["privateKeyPassphraseReference"])
                        .Controls.OfType<TextBox>().Single().Text);
            });

            var localDefaults = new ConnectionProviderDefaults(
                ConnectTimeoutSeconds: 12,
                OperationTimeoutSeconds: 45,
                MaximumRetryAttempts: 2,
                FieldValues: new Dictionary<string, string>());
            var draft = ConnectionEditorDraftFactory.Build(
                StorageProviderKind.Local,
                new Dictionary<string, string>
                {
                    ["profileName"] = "Local test",
                    ["rootPath"] = Path.GetTempPath()
                },
                localDefaults);
            Assert.Equal(12, draft.OperationalOptions.ConnectTimeoutSeconds);
            Assert.Equal(45, draft.OperationalOptions.OperationTimeoutSeconds);
            Assert.Equal(2, draft.OperationalOptions.MaximumRetryAttempts);
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
