using Krypton.Toolkit;

namespace StorageHub.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var kryptonManager = new KryptonManager
        {
            GlobalPaletteMode = PaletteMode.Microsoft365Blue,
            GlobalApplyToolstrips = false
        };
        System.Windows.Forms.Application.Run(new MainForm());
    }
}
