using System.Reflection;

namespace StorageHub.Desktop.Tests;

/// <summary>
/// The two dialogs, driven far enough to prove the decisions they are responsible for: what starts
/// ticked, what a dependency does to the ticks, when Export becomes usable, and that a wrong
/// password does not advance the wizard.
/// </summary>
public sealed class SettingsTransferFormTests
{
    private const string Password = "a good long password";

    [Fact]
    public void ExportStartsWithMachineSpecificUnticked()
    {
        WithExportForm((form, _) =>
        {
            // The file is shareable by default; paths and vault references have to be asked for.
            Assert.DoesNotContain(SettingsSectionId.MachineSpecific, form.SelectedSections);
            Assert.Contains(SettingsSectionId.DesktopGeneral, form.SelectedSections);
            Assert.Contains(SettingsSectionId.Connections, form.SelectedSections);
        });
    }

    [Fact]
    public void TickingSchedulesAlsoTicksWhatTheyDependOn()
    {
        WithExportForm((form, _) =>
        {
            Untick(form);
            Check(form, SettingsSectionId.Schedules).Checked = true;

            Assert.Contains(SettingsSectionId.Schedules, form.SelectedSections);
            Assert.Contains(SettingsSectionId.SyncProfiles, form.SelectedSections);
            Assert.Contains(SettingsSectionId.Connections, form.SelectedSections);
        });
    }

    [Fact]
    public void UntickingConnectionsUnticksWhateverDependedOnThem()
    {
        WithExportForm((form, _) =>
        {
            Check(form, SettingsSectionId.Schedules).Checked = true;
            Check(form, SettingsSectionId.Connections).Checked = false;

            Assert.DoesNotContain(SettingsSectionId.Connections, form.SelectedSections);
            Assert.DoesNotContain(SettingsSectionId.SyncProfiles, form.SelectedSections);
            Assert.DoesNotContain(SettingsSectionId.Schedules, form.SelectedSections);
        });
    }

    [Fact]
    public void ExportIsRefusedUntilThereIsSomethingToExport()
    {
        WithExportForm((form, _) =>
        {
            Assert.True(ExportButton(form).Enabled);

            Untick(form);

            Assert.False(ExportButton(form).Enabled);
        });
    }

    [Fact]
    public void ExportIsRefusedUntilTheTwoPasswordsAgree()
    {
        WithExportForm((form, _) =>
        {
            var protect = Field<CheckBox>(form, "_protect");
            var password = Field<TextBox>(form, "_password");
            var confirm = Field<TextBox>(form, "_confirm");

            protect.Checked = true;
            Assert.False(ExportButton(form).Enabled);

            password.Text = "short";
            Assert.False(ExportButton(form).Enabled);

            password.Text = Password;
            confirm.Text = "something else";
            Assert.False(ExportButton(form).Enabled);

            confirm.Text = Password;
            Assert.True(ExportButton(form).Enabled);

            // Clearing the tick must not leave a stale password behind to be written silently.
            protect.Checked = false;
            Assert.Empty(password.Text);
            Assert.True(ExportButton(form).Enabled);
        });
    }

    [Fact]
    public void ImportShowsOnlyWhatTheFileContains()
    {
        WithImportForm((form, fixture) =>
        {
            var path = Path.Combine(fixture.Directory, "one-section.shsettings");
            SettingsExportService.Write(
                path, fixture.Exporter.Capture([SettingsSectionId.Shortcuts]), password: null);

            form.LoadFile(path);
            Open(form);

            var sections = Field<Dictionary<SettingsSectionId, CheckBox>>(form, "_sections");
            Assert.True(sections[SettingsSectionId.Shortcuts].Enabled);
            Assert.True(sections[SettingsSectionId.Shortcuts].Checked);
            Assert.False(sections[SettingsSectionId.DesktopGeneral].Enabled);
            Assert.False(sections[SettingsSectionId.DesktopGeneral].Checked);
        });
    }

    [Fact]
    public void ImportLeavesMachineSpecificUntickedEvenWhenTheFileHasIt()
    {
        WithImportForm((form, fixture) =>
        {
            var path = Path.Combine(fixture.Directory, "everything.shsettings");
            SettingsExportService.Write(
                path,
                fixture.Exporter.Capture([.. Enum.GetValues<SettingsSectionId>()]),
                password: null);

            form.LoadFile(path);
            Open(form);

            var sections = Field<Dictionary<SettingsSectionId, CheckBox>>(form, "_sections");
            Assert.True(sections[SettingsSectionId.MachineSpecific].Enabled);
            Assert.False(sections[SettingsSectionId.MachineSpecific].Checked);
        });
    }

    [Fact]
    public void AWrongPasswordKeepsTheWizardOnTheFileStep()
    {
        WithImportForm((form, fixture) =>
        {
            var path = Path.Combine(fixture.Directory, "sealed.shsettings");
            SettingsExportService.Write(
                path, fixture.Exporter.Capture([SettingsSectionId.Shortcuts]), Password);

            form.LoadFile(path);
            Field<TextBox>(form, "_password").Text = "not the password";
            Open(form);

            // Still on step one, with the password box live so it can simply be retyped.
            Assert.Equal(SettingsImportStep.ChooseFile, form.Step);
            Assert.NotEqual(SettingsImportStep.Review, form.Step);

            Field<TextBox>(form, "_password").Text = Password;
            Open(form);

            Assert.Equal(SettingsImportStep.Review, form.Step);
        });
    }

    [Fact]
    public void AFileThatIsNotAnExportIsReportedWithoutOfferingToContinue()
    {
        WithImportForm((form, fixture) =>
        {
            var path = Path.Combine(fixture.Directory, "foreign.shsettings");
            File.WriteAllText(path, "this is not an export");

            form.LoadFile(path);
            Open(form);

            Assert.Equal(SettingsImportStep.ChooseFile, form.Step);
            Assert.NotEqual(SettingsImportStep.Review, form.Step);
        });
    }

    [Fact]
    public void ImportingAppliesOnlyTheTickedSections()
    {
        WithImportForm((form, fixture) =>
        {
            fixture.Store.Save(DesktopUpdatePreferences.Defaults with
            {
                Appearance = DesktopAppearance.Dark,
                ExternalEditorPath = @"C:\Tools\local.exe"
            });
            var source = new DesktopUpdatePreferencesStore(
                Path.Combine(fixture.Directory, "source.json"));
            source.Save(DesktopUpdatePreferences.Defaults with
            {
                Appearance = DesktopAppearance.Light,
                ExternalEditorPath = @"C:\Tools\imported.exe"
            });
            var path = Path.Combine(fixture.Directory, "source.shsettings");
            SettingsExportService.Write(
                path,
                new SettingsExportService(source).Capture([.. Enum.GetValues<SettingsSectionId>()]),
                password: null);

            form.LoadFile(path);
            Open(form);
            // Desktop preferences only: the machine-specific tick is left where it starts.
            Field<Dictionary<SettingsSectionId, CheckBox>>(form, "_sections")[SettingsSectionId.Shortcuts]
                .Checked = false;
            form.ApplyImportAsync().GetAwaiter().GetResult();

            var report = Assert.IsType<SettingsImportReport>(form.Report);
            Assert.True(report.Succeeded);
            // The result screen has to still be showing. Assigning DialogResult inside the apply
            // would close the form immediately and the user would never see what happened.
            Assert.Equal(SettingsImportStep.Result, form.Step);
            Assert.False(form.IsDisposed);
            var saved = fixture.Store.Load();
            Assert.Equal(DesktopAppearance.Light, saved.Appearance);
            Assert.Equal(@"C:\Tools\local.exe", saved.ExternalEditorPath);
            Assert.NotNull(report.BackupPath);
        });
    }

    private static void WithExportForm(Action<SettingsExportForm, TransferFixture> assert) =>
        WithFixture(fixture =>
        {
            using var form = new SettingsExportForm(fixture.Exporter);
            assert(form, fixture);
        });

    private static void WithImportForm(Action<SettingsImportForm, TransferFixture> assert) =>
        WithFixture(fixture =>
        {
            using var form = new SettingsImportForm(fixture.Importer);
            assert(form, fixture);
        });

    private static void WithFixture(Action<TransferFixture> assert) =>
        SyncRunReviewControlTests.RunOnSta(() =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(), $"storagehub-settings-forms-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var store = new DesktopUpdatePreferencesStore(Path.Combine(directory, "settings.json"));
                var exporter = new SettingsExportService(
                    store,
                    clock: () => DateTimeOffset.UnixEpoch,
                    application: () => "StorageHub test",
                    fingerprint: () => "test-fingerprint");
                assert(new TransferFixture(
                    directory,
                    store,
                    exporter,
                    new SettingsImportService(
                        store,
                        exporter,
                        Path.Combine(directory, "backups"),
                        () => DateTimeOffset.UnixEpoch)));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

    private static void Untick(SettingsExportForm form)
    {
        foreach (var check in Field<Dictionary<SettingsSectionId, CheckBox>>(form, "_sections").Values)
        {
            check.Checked = false;
        }
    }

    private static CheckBox Check(SettingsExportForm form, SettingsSectionId id) =>
        Field<Dictionary<SettingsSectionId, CheckBox>>(form, "_sections")[id];

    private static Button ExportButton(SettingsExportForm form) => Field<Button>(form, "_export");

    private static void Open(SettingsImportForm form) => Invoke(form, "OpenSelectedFile");

    private static void Invoke(object instance, string method) => instance.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(instance, null);

    private static T Field<T>(object instance, string name)
        where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }

    private sealed record TransferFixture(
        string Directory,
        DesktopUpdatePreferencesStore Store,
        SettingsExportService Exporter,
        SettingsImportService Importer);
}
