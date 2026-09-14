namespace StorageHub.Desktop.Tests;

/// <summary>
/// Capture and apply are two halves of one mapping. These cover the half that is easy to get
/// wrong: leaving settings alone that the user did not ask to import.
/// </summary>
public sealed class DesktopPreferenceSectionMapperTests
{
    /// <summary>
    /// Properties that are deliberately not part of any exportable section, with the reason.
    /// Everything else must be claimed by exactly one section, or it silently fails to export.
    /// </summary>
    private static readonly Dictionary<string, string> Unexported = new(StringComparer.Ordinal)
    {
        // Carried by their own sections rather than by the desktop record's shape.
        [nameof(DesktopUpdatePreferences.Shortcuts)] = "its own section",
        [nameof(DesktopUpdatePreferences.ConnectionDefaults)] = "its own section",
        [nameof(DesktopUpdatePreferences.ExternalEditorPath)] = "machine-specific section",
        [nameof(DesktopUpdatePreferences.PinnedWorkspaces)] = "machine-specific section",
        [nameof(DesktopUpdatePreferences.RecentWorkspaces)] = "machine-specific section",

        // Window layout belongs to the machine it was sized on, exactly as the workspace lists do.
        [nameof(DesktopUpdatePreferences.ConnectionsPanelWidth)] = "machine-specific window layout",
        [nameof(DesktopUpdatePreferences.ConnectionsPanelVisible)] = "machine-specific window layout",
        [nameof(DesktopUpdatePreferences.ConnectionsPanelSide)] = "machine-specific window layout"
    };

    [Fact]
    public void EveryPreferencePropertyIsCarriedBySomeSection()
    {
        // The guard against a twenty-fifth preference being added and silently not exported.
        var general = typeof(DesktopGeneralSection).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var unclaimed = typeof(DesktopUpdatePreferences)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => !string.Equals(name, "EqualityContract", StringComparison.Ordinal))
            .Where(name => !general.Contains(name) && !Unexported.ContainsKey(name))
            .ToArray();

        Assert.Empty(unclaimed);
    }

    [Fact]
    public void CapturingAndApplyingAGeneralSectionIsALosslessRoundTrip()
    {
        var source = Customised();
        var document = Document() with { DesktopGeneral = DesktopPreferenceSectionMapper.CaptureGeneral(source) };

        var result = DesktopPreferenceSectionMapper.Apply(
            DesktopUpdatePreferences.Defaults, document, [SettingsSectionId.DesktopGeneral]);

        Assert.Empty(result.Blocked);
        Assert.Equal(
            DesktopPreferenceSectionMapper.CaptureGeneral(source),
            DesktopPreferenceSectionMapper.CaptureGeneral(result.Preferences));
    }

    [Fact]
    public void ImportingNothingChangesNothing()
    {
        var current = Customised();

        var result = DesktopPreferenceSectionMapper.Apply(current, FullDocument(), []);

        Assert.Same(current, result.Preferences);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public void ImportingOnlyShortcutsLeavesEveryOtherSettingAlone()
    {
        var current = Customised();
        var shortcuts = ShortcutSettings.Resolve(null);
        var document = Document() with
        {
            Shortcuts = shortcuts,
            DesktopGeneral = DesktopPreferenceSectionMapper.CaptureGeneral(DesktopUpdatePreferences.Defaults)
        };

        var result = DesktopPreferenceSectionMapper.Apply(current, document, [SettingsSectionId.Shortcuts]);

        Assert.Equal([SettingsSectionId.Shortcuts], result.Applied);
        Assert.Equal(
            DesktopPreferenceSectionMapper.CaptureGeneral(current),
            DesktopPreferenceSectionMapper.CaptureGeneral(result.Preferences));
        Assert.Equal(current.ExternalEditorPath, result.Preferences.ExternalEditorPath);
        Assert.Equal(shortcuts.Count, result.Preferences.Shortcuts!.Count);
    }

    [Fact]
    public void ImportingConnectionDefaultsKeepsThisMachinesPrivateKeyReference()
    {
        // Normalize rebuilds the complete key set, so overlaying the imported subset onto the
        // current dictionary is the only thing standing between an import and a wiped key
        // reference. Without this, ticking "connection defaults" would silently break SFTP.
        var current = WithPrivateKey(LocalReference);
        var document = Document() with
        {
            ConnectionDefaults = DesktopPreferenceSectionMapper.CaptureConnectionDefaults(
                WithPrivateKey(ExportedReference) with
                {
                    ConnectionDefaults = Defaults("Sftp.port", "2222")
                })
        };

        var result = DesktopPreferenceSectionMapper.Apply(
            current, document, [SettingsSectionId.ConnectionDefaults]);

        Assert.Equal(LocalReference, result.Preferences.ConnectionDefaults![PrivateKeyKey]);
        Assert.Equal("2222", result.Preferences.ConnectionDefaults["Sftp.port"]);
    }

    [Fact]
    public void APrivateKeyReferenceIsNeverCapturedIntoTheSharedSection()
    {
        var captured = DesktopPreferenceSectionMapper.CaptureConnectionDefaults(
            WithPrivateKey("shs_reference"));

        Assert.DoesNotContain(
            captured.Keys,
            key => key.EndsWith(".privateKeyReference", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMachineSpecificSectionCarriesThePrivateKeyReferenceAndRestoresIt()
    {
        var source = WithPrivateKey(ExportedReference);
        var document = Document() with
        {
            MachineSpecific = DesktopPreferenceSectionMapper.CaptureMachineSpecific(source)
        };

        var result = DesktopPreferenceSectionMapper.Apply(
            WithPrivateKey(LocalReference), document, [SettingsSectionId.MachineSpecific]);

        Assert.Equal(ExportedReference, result.Preferences.ConnectionDefaults![PrivateKeyKey]);
    }

    [Fact]
    public void MachineSpecificRestoresTheEditorPathAndWorkspaceLists()
    {
        var pinned = new WorkspaceShortcutEntry(@"C:\work\nightly.shw", "Nightly", DateTimeOffset.UnixEpoch);
        var document = Document() with
        {
            MachineSpecific = new MachineSpecificSection(@"C:\Tools\editor.exe", [pinned], [], null)
        };

        var result = DesktopPreferenceSectionMapper.Apply(
            DesktopUpdatePreferences.Defaults, document, [SettingsSectionId.MachineSpecific]);

        Assert.Equal(@"C:\Tools\editor.exe", result.Preferences.ExternalEditorPath);
        Assert.Equal(pinned, Assert.Single(result.Preferences.PinnedWorkspaces!));
        Assert.Empty(result.Preferences.RecentWorkspaces!);
    }

    [Fact]
    public void AnUnusableEditorPathIsReportedAndTheCurrentOneKept()
    {
        var current = DesktopUpdatePreferences.Defaults with { ExternalEditorPath = @"C:\Tools\editor.exe" };
        var document = Document() with
        {
            MachineSpecific = new MachineSpecificSection(@"relative\editor.exe", null, null, null)
        };

        var result = DesktopPreferenceSectionMapper.Apply(
            current, document, [SettingsSectionId.MachineSpecific]);

        Assert.Equal(@"C:\Tools\editor.exe", result.Preferences.ExternalEditorPath);
        Assert.Contains(SettingsSectionId.MachineSpecific, result.Blocked.Keys);
    }

    [Fact]
    public void AWorkspacePathThatNoLongerMakesSenseIsDroppedRatherThanBlockingTheSection()
    {
        var document = Document() with
        {
            MachineSpecific = new MachineSpecificSection(
                null,
                [
                    new WorkspaceShortcutEntry(@"relative\bad.shw", "Bad", DateTimeOffset.UnixEpoch),
                    new WorkspaceShortcutEntry(@"C:\work\good.shw", "Good", DateTimeOffset.UnixEpoch)
                ],
                null,
                null)
        };

        var result = DesktopPreferenceSectionMapper.Apply(
            DesktopUpdatePreferences.Defaults, document, [SettingsSectionId.MachineSpecific]);

        Assert.Equal(@"C:\work\good.shw", Assert.Single(result.Preferences.PinnedWorkspaces!).Path);
        Assert.Empty(result.Blocked);
    }

    [Fact]
    public void InvalidShortcutsAreReportedRatherThanSilentlyResettingThemAll()
    {
        // ShortcutSettings.Resolve falls back to the full default set for an invalid map. During
        // an import that would throw away every shortcut the user had, without a word.
        var current = Customised();
        var conflicting = ShortcutSettings.Resolve(null);
        var first = conflicting.Keys.First();
        conflicting[conflicting.Keys.Skip(1).First()] = conflicting[first];
        var document = Document() with { Shortcuts = conflicting };

        var result = DesktopPreferenceSectionMapper.Apply(current, document, [SettingsSectionId.Shortcuts]);

        Assert.Empty(result.Applied);
        Assert.Contains(SettingsSectionId.Shortcuts, result.Blocked.Keys);
        Assert.Equal(current.Shortcuts, result.Preferences.Shortcuts);
    }

    [Fact]
    public void AGeneralSectionOutsideTheBoundsSaveEnforcesIsRefusedWhole()
    {
        var current = Customised();
        var document = Document() with
        {
            DesktopGeneral = DesktopPreferenceSectionMapper.CaptureGeneral(DesktopUpdatePreferences.Defaults) with
            {
                MaximumTransferConcurrency = 9_999
            }
        };

        var result = DesktopPreferenceSectionMapper.Apply(current, document, [SettingsSectionId.DesktopGeneral]);

        Assert.Empty(result.Applied);
        Assert.Contains(SettingsSectionId.DesktopGeneral, result.Blocked.Keys);
        // Refused whole: nothing from the section leaked through.
        Assert.Equal(current.Appearance, result.Preferences.Appearance);
    }

    [Fact]
    public void AnUndefinedEnumValueFallsBackToTheCurrentOne()
    {
        var current = DesktopUpdatePreferences.Defaults with { Appearance = DesktopAppearance.Dark };
        var document = Document() with
        {
            DesktopGeneral = DesktopPreferenceSectionMapper.CaptureGeneral(DesktopUpdatePreferences.Defaults) with
            {
                Appearance = (DesktopAppearance)99,
                SshHostKeyDiscovery = (SshHostKeyDiscoveryMode)99
            }
        };

        var result = DesktopPreferenceSectionMapper.Apply(current, document, [SettingsSectionId.DesktopGeneral]);

        Assert.Equal(DesktopAppearance.Dark, result.Preferences.Appearance);
        Assert.Equal(current.SshHostKeyDiscovery, result.Preferences.SshHostKeyDiscovery);
    }

    [Fact]
    public void ASectionSelectedButAbsentFromTheFileIsSimplyNotApplied()
    {
        var current = Customised();

        var result = DesktopPreferenceSectionMapper.Apply(
            current, Document(), [.. Enum.GetValues<SettingsSectionId>()]);

        Assert.Empty(result.Applied);
        Assert.Empty(result.Blocked);
        Assert.Same(current, result.Preferences);
    }

    private const string PrivateKeyKey = "Sftp.privateKeyReference";

    // A vault reference is exactly "shs_" plus 43 base64url characters; anything else is dropped
    // by the connection-default validator, which would mask what these tests are checking.
    private const string LocalReference = "shs_" + "local00000000000000000000000000000000000000";
    private const string ExportedReference = "shs_" + "exported00000000000000000000000000000000000";

    private static SettingsExportDocument Document() =>
        SettingsExportSerializer.Create(DateTimeOffset.UnixEpoch, "StorageHub", null);

    private static SettingsExportDocument FullDocument() => Document() with
    {
        DesktopGeneral = DesktopPreferenceSectionMapper.CaptureGeneral(DesktopUpdatePreferences.Defaults),
        Shortcuts = ShortcutSettings.Resolve(null),
        ConnectionDefaults = ConnectionDefaultSettings.Normalize(null),
        MachineSpecific = new MachineSpecificSection(@"C:\other\editor.exe", [], [], null)
    };

    private static DesktopUpdatePreferences Customised() => DesktopUpdatePreferences.Defaults with
    {
        Appearance = DesktopAppearance.Dark,
        MinimumConcurrency = 2,
        MaximumTransferConcurrency = 8,
        PerConnectionConcurrency = 4,
        MaximumSyncConcurrency = 3,
        AdaptiveConcurrency = false,
        DefaultWorkspaceLayout = WorkspaceLayout.TopAndBottom,
        DefaultWorkspacePaneCount = 3,
        ConfirmBeforeDeletingItems = false,
        ExternalEditorPath = @"C:\Tools\current.exe",
        Shortcuts = ShortcutSettings.Resolve(null),
        SshTerminal = new SshTerminalPreferences("screen-256color")
    };

    private static DesktopUpdatePreferences WithPrivateKey(string reference) =>
        DesktopUpdatePreferences.Defaults with { ConnectionDefaults = Defaults(PrivateKeyKey, reference) };

    private static Dictionary<string, string> Defaults(string key, string value)
    {
        var defaults = ConnectionDefaultSettings.Normalize(null);
        defaults[key] = value;
        return defaults;
    }
}
