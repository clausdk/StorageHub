namespace StorageHub.Desktop.Tests;

public sealed class DesktopUpdaterTests
{
    [Fact]
    public void PreferencesRoundTripThroughAtomicStore()
    {
        using var fixture = new SettingsFixture();
        var expected = new DesktopUpdatePreferences(
            CheckAutomatically: false,
            DownloadAutomatically: false,
            RestartAutomatically: true,
            IncludePrereleases: false,
            SshHostKeyDiscoveryMode.Automatic);

        fixture.Store.Save(expected);

        Assert.Equal(expected, fixture.Store.Load());
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":999,\"checkAutomatically\":false}")]
    public void MissingMalformedOrUnsupportedPreferencesFailSafeToDefaults(string contents)
    {
        using var fixture = new SettingsFixture();
        if (contents.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path)!);
            File.WriteAllText(fixture.Path, contents);
        }

        Assert.Equal(DesktopUpdatePreferences.Defaults, fixture.Store.Load());
    }

    [Fact]
    public void OversizedPreferencesFailSafeWithoutParsingAttackerControlledContent()
    {
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path)!);
        File.WriteAllBytes(fixture.Path, new byte[(64 * 1024) + 1]);

        Assert.Equal(DesktopUpdatePreferences.Defaults, fixture.Store.Load());
    }

    [Fact]
    public void PreferencesStoreRejectsRelativePaths()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new DesktopUpdatePreferencesStore("settings.json"));
    }

    [Fact]
    public void VersionOnePreferencesMigrateWithoutLosingUpdateChoices()
    {
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path)!);
        File.WriteAllText(
            fixture.Path,
            """
            {
              "schemaVersion": 1,
              "checkAutomatically": false,
              "downloadAutomatically": false,
              "restartAutomatically": false,
              "includePrereleases": false
            }
            """);

        var migrated = fixture.Store.Load();

        Assert.False(migrated.CheckAutomatically);
        Assert.False(migrated.DownloadAutomatically);
        Assert.False(migrated.IncludePrereleases);
        Assert.Equal(SshHostKeyDiscoveryMode.AskBeforeFetching, migrated.SshHostKeyDiscovery);
    }

    [Fact]
    public void UndefinedDiscoveryModeFailsSafeToDefaults()
    {
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path)!);
        File.WriteAllText(
            fixture.Path,
            """
            {
              "schemaVersion": 2,
              "checkAutomatically": false,
              "downloadAutomatically": false,
              "restartAutomatically": false,
              "includePrereleases": false,
              "sshHostKeyDiscovery": 999
            }
            """);

        Assert.Equal(DesktopUpdatePreferences.Defaults, fixture.Store.Load());
    }

    [Fact]
    public async Task DisabledAutomaticChecksNeverConstructAnUpdateEngineOrContactGitHub()
    {
        using var fixture = new SettingsFixture();
        fixture.Store.Save(DesktopUpdatePreferences.Defaults with { CheckAutomatically = false });
        var factory = new FakeEngineFactory();
        var updater = new DesktopUpdater(fixture.Store, factory);

        await updater.RunAutomaticAsync(CancellationToken.None);

        Assert.Equal(DesktopUpdateState.Disabled, updater.Snapshot.State);
        Assert.Empty(factory.CreatedEngines);
    }

    [Fact]
    public async Task DeveloperAndPortableBuildsFailClosedBeforeAnyNetworkCheck()
    {
        using var fixture = new SettingsFixture();
        var engine = new FakeUpdateEngine { IsInstalled = false };
        var updater = new DesktopUpdater(fixture.Store, new FakeEngineFactory(engine));

        var snapshot = await updater.CheckForUpdatesAsync(CancellationToken.None);

        Assert.Equal(DesktopUpdateState.Unavailable, snapshot.State);
        Assert.Equal(0, engine.CheckCalls);
        Assert.Equal(0, engine.DownloadCalls);
        Assert.Equal(0, engine.ApplyCalls);
    }

    [Fact]
    public async Task AutomaticFlowDownloadsExactCandidateAndRestartsOnlyWhenExplicitlyEnabled()
    {
        using var fixture = new SettingsFixture();
        fixture.Store.Save(DesktopUpdatePreferences.Defaults with { RestartAutomatically = true });
        var engine = new FakeUpdateEngine();
        var updater = new DesktopUpdater(fixture.Store, new FakeEngineFactory(engine));
        var restartRequests = 0;
        updater.RestartRequested += (_, _) => restartRequests++;

        await updater.RunAutomaticAsync(CancellationToken.None);

        Assert.Equal(DesktopUpdateState.Installing, updater.Snapshot.State);
        Assert.Same(engine.AvailableCandidate, Assert.Single(engine.DownloadedCandidates));
        Assert.Same(engine.AvailableCandidate, Assert.Single(engine.AppliedCandidates));
        Assert.Equal(1, restartRequests);
        Assert.False(updater.ApplyAndRestart());
        Assert.Single(engine.AppliedCandidates);
    }

    [Fact]
    public async Task DefaultAutomaticFlowDownloadsButDoesNotInterruptTheApplication()
    {
        using var fixture = new SettingsFixture();
        var engine = new FakeUpdateEngine();
        var updater = new DesktopUpdater(fixture.Store, new FakeEngineFactory(engine));

        await updater.RunAutomaticAsync(CancellationToken.None);

        Assert.Equal(DesktopUpdateState.ReadyToRestart, updater.Snapshot.State);
        Assert.Single(engine.DownloadedCandidates);
        Assert.Empty(engine.AppliedCandidates);
    }

    [Fact]
    public async Task ApplyIsRejectedUntilTheSelectedCandidateHasDownloadedSuccessfully()
    {
        using var fixture = new SettingsFixture();
        var engine = new FakeUpdateEngine();
        var updater = new DesktopUpdater(fixture.Store, new FakeEngineFactory(engine));

        var checkedSnapshot = await updater.CheckForUpdatesAsync(CancellationToken.None);
        var applied = updater.ApplyAndRestart();

        Assert.Equal(DesktopUpdateState.UpdateAvailable, checkedSnapshot.State);
        Assert.False(applied);
        Assert.Empty(engine.AppliedCandidates);
    }

    [Fact]
    public async Task FailedChecksDoNotExposeRemoteExceptionDetailsOrLeaveAnApplicableCandidate()
    {
        using var fixture = new SettingsFixture();
        var sensitiveDetail = "token=secret-value C:\\Users\\person\\private";
        var engine = new FakeUpdateEngine { CheckError = new IOException(sensitiveDetail) };
        var updater = new DesktopUpdater(fixture.Store, new FakeEngineFactory(engine));

        var snapshot = await updater.CheckForUpdatesAsync(CancellationToken.None);

        Assert.Equal(DesktopUpdateState.Failed, snapshot.State);
        Assert.DoesNotContain("secret-value", snapshot.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", snapshot.Message, StringComparison.Ordinal);
        Assert.False(updater.ApplyAndRestart());
        Assert.Empty(engine.AppliedCandidates);
    }

    [Fact]
    public async Task ConcurrentCheckIsFencedAndCannotReplaceTheInFlightCandidate()
    {
        using var fixture = new SettingsFixture();
        var releaseCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeUpdateEngine { CheckBlock = releaseCheck.Task };
        var updater = new DesktopUpdater(fixture.Store, new FakeEngineFactory(engine));

        var first = updater.CheckForUpdatesAsync(CancellationToken.None);
        await engine.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await updater.CheckForUpdatesAsync(CancellationToken.None);
        releaseCheck.SetResult();
        _ = await first;

        Assert.Equal(DesktopUpdateState.Checking, second.State);
        Assert.Equal(1, engine.CheckCalls);
    }

    [Fact]
    public async Task DisablingAutomaticChecksCancelsAnInFlightAutomaticOperation()
    {
        using var fixture = new SettingsFixture();
        var releaseCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeUpdateEngine { CheckBlock = releaseCheck.Task };
        using var updater = new DesktopUpdater(fixture.Store, new FakeEngineFactory(engine));
        var automatic = updater.RunAutomaticAsync(CancellationToken.None);
        await engine.CheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        updater.SavePreferences(updater.Preferences with { CheckAutomatically = false });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => automatic);
        Assert.Equal(DesktopUpdateState.Disabled, updater.Snapshot.State);
        Assert.Equal(0, engine.DownloadCalls);
        Assert.Equal(0, engine.ApplyCalls);
    }

    [Fact]
    public void GithubSourceAndDowngradePolicyAreNotUserConfigurable()
    {
        Assert.Equal(
            "https://github.com/clausdk/StorageHub",
            VelopackDesktopUpdateEngineFactory.TrustedRepositoryUrl);
        Assert.DoesNotContain(
            typeof(DesktopUpdatePreferences).GetProperties(),
            property => property.Name.Contains("Url", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Downgrade", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeEngineFactory : IDesktopUpdateEngineFactory
    {
        private readonly FakeUpdateEngine? _engine;

        internal FakeEngineFactory(FakeUpdateEngine? engine = null) => _engine = engine;

        internal List<IDesktopUpdateEngine> CreatedEngines { get; } = [];

        public IDesktopUpdateEngine Create(bool includePrereleases)
        {
            var engine = _engine ?? new FakeUpdateEngine();
            engine.IncludePrereleases = includePrereleases;
            CreatedEngines.Add(engine);
            return engine;
        }
    }

    private sealed class FakeUpdateEngine : IDesktopUpdateEngine
    {
        internal DesktopUpdateCandidate AvailableCandidate { get; } =
            new("0.1.0-preview.999", new object());

        internal bool IncludePrereleases { get; set; }

        public bool IsInstalled { get; init; } = true;

        public string CurrentVersion => "0.1.0-preview.1";

        internal int CheckCalls { get; private set; }

        internal int DownloadCalls { get; private set; }

        internal int ApplyCalls { get; private set; }

        internal Exception? CheckError { get; init; }

        internal Task? CheckBlock { get; init; }

        internal TaskCompletionSource CheckStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<DesktopUpdateCandidate> DownloadedCandidates { get; } = [];

        internal List<DesktopUpdateCandidate> AppliedCandidates { get; } = [];

        public async Task<DesktopUpdateCandidate?> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            CheckCalls++;
            CheckStarted.TrySetResult();
            if (CheckBlock is not null)
            {
                await CheckBlock.WaitAsync(cancellationToken);
            }

            if (CheckError is not null)
            {
                throw CheckError;
            }

            return AvailableCandidate;
        }

        public Task DownloadAsync(
            DesktopUpdateCandidate candidate,
            IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCalls++;
            DownloadedCandidates.Add(candidate);
            progress.Report(100);
            return Task.CompletedTask;
        }

        public void PrepareSilentApplyAndRestart(DesktopUpdateCandidate candidate)
        {
            ApplyCalls++;
            AppliedCandidates.Add(candidate);
        }
    }

    private sealed class SettingsFixture : IDisposable
    {
        internal SettingsFixture()
        {
            Directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "StorageHub updater tests",
                Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(Directory, "Desktop", "settings.json");
            Store = new DesktopUpdatePreferencesStore(Path);
        }

        internal string Directory { get; }

        internal string Path { get; }

        internal DesktopUpdatePreferencesStore Store { get; }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
    }
}
