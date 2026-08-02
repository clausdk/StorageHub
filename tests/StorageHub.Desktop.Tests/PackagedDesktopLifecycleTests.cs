namespace StorageHub.Desktop.Tests;

public sealed class PackagedDesktopLifecycleTests
{
    [Fact]
    public void ConfigureAutostartRegistersAgentOnlyDesktopCommandForCurrentUser()
    {
        var fixture = CreateFixture();

        var result = fixture.Lifecycle.ConfigureAutostart();

        Assert.Equal(AutostartConfigurationStatus.Registered, result);
        var entry = Assert.Single(fixture.RunEntries.SetCalls);
        Assert.Equal("StorageHub.Agent", entry.Name);
        Assert.Equal($"\"{fixture.DesktopExecutable}\" --agent-only", entry.CommandLine);
        Assert.Empty(fixture.RunEntries.RemovedNames);
    }

    [Fact]
    public void ConfigureAutostartDisableSwitchRemovesAnyExistingEntry()
    {
        var fixture = CreateFixture(disableAutostart: true);

        var result = fixture.Lifecycle.ConfigureAutostart();

        Assert.Equal(AutostartConfigurationStatus.Disabled, result);
        Assert.Empty(fixture.RunEntries.SetCalls);
        Assert.Equal("StorageHub.Agent", Assert.Single(fixture.RunEntries.RemovedNames));
    }

    [Fact]
    public async Task EnsureAgentDoesNotLaunchWhenPipeIsAlreadyAvailable()
    {
        var fixture = CreateFixture(agentAvailable: true);

        var result = await fixture.Lifecycle.EnsureAgentAsync();

        Assert.Equal(AgentEnsureStatus.AlreadyRunning, result.Status);
        Assert.True(result.IsReady);
        Assert.Empty(fixture.Launcher.Launches);
        Assert.Equal(0, fixture.AgentClient.WaitCalls);
    }

    [Fact]
    public async Task EnsureAgentLaunchesPackagedSiblingHiddenAndWaitsForReadiness()
    {
        var fixture = CreateFixture(agentAvailable: false, fileExists: true, waitResult: true);

        var result = await fixture.Lifecycle.EnsureAgentAsync();

        Assert.Equal(AgentEnsureStatus.Started, result.Status);
        var launch = Assert.Single(fixture.Launcher.Launches);
        Assert.Equal(Path.Combine(fixture.ApplicationDirectory, "Agent", "StorageHub.Agent.Windows.exe"), launch.Executable);
        Assert.Equal(Path.Combine(fixture.ApplicationDirectory, "Agent"), launch.WorkingDirectory);
        Assert.Equal("--background", launch.Argument);
        Assert.Equal(1, fixture.AgentClient.WaitCalls);
    }

    [Fact]
    public async Task EnsureAgentFailsClosedWhenPackagedExecutableIsMissing()
    {
        var fixture = CreateFixture(agentAvailable: false, fileExists: false);

        var result = await fixture.Lifecycle.EnsureAgentAsync();

        Assert.Equal(AgentEnsureStatus.MissingExecutable, result.Status);
        Assert.False(result.IsReady);
        Assert.Empty(fixture.Launcher.Launches);
        Assert.Equal(0, fixture.AgentClient.WaitCalls);
    }

    [Fact]
    public void VelopackHooksRegisterRefreshStopAndUnregisterWithoutADataDeletionSurface()
    {
        var fixture = CreateFixture(shutdownResult: true);
        var hooks = new DesktopPackageLifecycleHooks(fixture.Lifecycle);

        hooks.AfterInstall();
        hooks.BeforeUpdate();
        hooks.AfterUpdate();
        hooks.BeforeUninstall();

        Assert.Equal(2, fixture.RunEntries.SetCalls.Count);
        Assert.Equal("StorageHub.Agent", Assert.Single(fixture.RunEntries.RemovedNames));
        Assert.Equal(
            [AgentShutdownReason.Update, AgentShutdownReason.Uninstall],
            fixture.AgentClient.ShutdownReasons);
    }

    [Fact]
    public void VelopackBootstrapDisablesFrameworkAutoApplySoUpdaterPreferencesRemainAuthoritative()
    {
        Assert.False(VelopackDesktopBootstrap.AutoApplyOnStartup);
    }

    [Theory]
    [InlineData("--agent-only")]
    [InlineData("--AGENT-ONLY")]
    public void AgentOnlyArgumentIsCaseInsensitive(string argument)
    {
        Assert.True(DesktopCommandLine.IsAgentOnly([argument]));
        Assert.False(DesktopCommandLine.IsAgentOnly(["--health"]));
    }

    [Fact]
    public void ApplicationVersionComesFromAssemblyMetadata()
    {
        var assembly = typeof(PackagedDesktopLifecycle).Assembly;

        var version = DesktopApplicationVersion.Resolve(assembly);
        var expected = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .SingleOrDefault()?
            .InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        var metadataSeparator = expected.IndexOf('+', StringComparison.Ordinal);
        if (metadataSeparator >= 0)
        {
            expected = expected[..metadataSeparator];
        }

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain(version, char.IsControl);
        Assert.Equal(expected, version);
    }

    private static LifecycleFixture CreateFixture(
        bool disableAutostart = false,
        bool agentAvailable = false,
        bool fileExists = true,
        bool waitResult = false,
        bool shutdownResult = false)
    {
        var applicationDirectory = Path.Combine(
            Path.GetTempPath(),
            "StorageHub lifecycle fixture",
            Guid.NewGuid().ToString("N"));
        var desktopExecutable = Path.Combine(applicationDirectory, "StorageHub.Desktop.exe");
        var runEntries = new FakeRunEntryStore();
        var launcher = new FakeProcessLauncher();
        var agentClient = new FakeAgentLifecycleClient
        {
            Available = agentAvailable,
            WaitResult = waitResult,
            ShutdownResult = shutdownResult
        };
        var lifecycle = new PackagedDesktopLifecycle(
            desktopExecutable,
            applicationDirectory,
            runEntries,
            launcher,
            agentClient,
            name => disableAutostart && string.Equals(
                name,
                PackagedDesktopLifecycleOptions.DisableAutostartEnvironmentVariable,
                StringComparison.Ordinal)
                    ? "1"
                    : null,
            _ => fileExists);
        return new LifecycleFixture(
            lifecycle,
            runEntries,
            launcher,
            agentClient,
            applicationDirectory,
            desktopExecutable);
    }

    private sealed record LifecycleFixture(
        PackagedDesktopLifecycle Lifecycle,
        FakeRunEntryStore RunEntries,
        FakeProcessLauncher Launcher,
        FakeAgentLifecycleClient AgentClient,
        string ApplicationDirectory,
        string DesktopExecutable);

    private sealed class FakeRunEntryStore : ICurrentUserRunEntryStore
    {
        public List<(string Name, string CommandLine)> SetCalls { get; } = [];

        public List<string> RemovedNames { get; } = [];

        public void SetValue(string valueName, string commandLine) =>
            SetCalls.Add((valueName, commandLine));

        public void Remove(string valueName) => RemovedNames.Add(valueName);
    }

    private sealed class FakeProcessLauncher : IAgentProcessLauncher
    {
        public List<(string Executable, string WorkingDirectory, string Argument)> Launches { get; } = [];

        public bool TryLaunchHidden(string executablePath, string workingDirectory, string argument)
        {
            Launches.Add((executablePath, workingDirectory, argument));
            return true;
        }
    }

    private sealed class FakeAgentLifecycleClient : IPackagedAgentLifecycleClient
    {
        public bool Available { get; init; }

        public bool WaitResult { get; init; }

        public bool ShutdownResult { get; init; }

        public int WaitCalls { get; private set; }

        public List<AgentShutdownReason> ShutdownReasons { get; } = [];

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Available);
        }

        public ValueTask<bool> WaitUntilAvailableAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCalls++;
            return ValueTask.FromResult(WaitResult);
        }

        public ValueTask<bool> RequestShutdownAndWaitAsync(
            AgentShutdownReason reason,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShutdownReasons.Add(reason);
            return ValueTask.FromResult(ShutdownResult);
        }
    }
}
