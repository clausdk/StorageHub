
namespace StorageHub.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        VelopackDesktopBootstrap.Build(args).Run();

        using var lifecycle = PackagedDesktopLifecycle.CreateDefault();
        var agentOnly = DesktopCommandLine.IsAgentOnly(args);
        if (agentOnly && lifecycle.IsAutostartDisabled)
        {
            _ = lifecycle.RemoveAutostart();
            return 0;
        }

        var agent = lifecycle.EnsureAgentAsync().AsTask().GetAwaiter().GetResult();
        if (agentOnly)
        {
            return agent.IsReady ? 0 : 1;
        }

        ApplicationConfiguration.Initialize();
        var explorerDropBrokerAvailable = ExplorerDropBrokerInstaller.EnsureRegistered(AppContext.BaseDirectory);
        if (!agent.IsReady)
        {
            _ = MessageBox.Show(
                DesktopStartupPreflight.DescribeFailure(agent.Status),
                "StorageHub startup check",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        var preferencesStore = DesktopUpdatePreferencesStore.CreateDefault();
        DesktopAppearanceService.SetAppearance(preferencesStore.Load().Appearance);
        System.Windows.Forms.Application.Run(new MainForm(
            preferencesStore,
            updateEngineFactory: null,
            lifecycle,
            explorerDropBrokerAvailable));
        return 0;
    }
}

internal static class DesktopStartupPreflight
{
    internal static string DescribeFailure(AgentEnsureStatus status) => status switch
    {
        AgentEnsureStatus.MissingExecutable =>
            "The StorageHub background agent is missing. Repair or reinstall StorageHub, then try again.",
        AgentEnsureStatus.StartupTimedOut =>
            "The StorageHub background agent did not become ready in time. Close any stuck StorageHub processes and try again.",
        AgentEnsureStatus.LaunchFailed =>
            "The StorageHub background agent could not be started. Check Windows security settings and the StorageHub installation, then try again.",
        _ => "The StorageHub background agent is not ready. Try starting StorageHub again."
    };
}
