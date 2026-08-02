using Krypton.Toolkit;

namespace StorageHub.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        VelopackDesktopBootstrap.Build(args).Run();

        var lifecycle = PackagedDesktopLifecycle.CreateDefault();
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
        using var kryptonManager = new KryptonManager
        {
            GlobalPaletteMode = PaletteMode.Microsoft365Blue,
            GlobalApplyToolstrips = false
        };
        System.Windows.Forms.Application.Run(new MainForm());
        return 0;
    }
}
