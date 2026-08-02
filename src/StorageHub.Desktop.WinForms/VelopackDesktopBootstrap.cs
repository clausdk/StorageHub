using Velopack;

namespace StorageHub.Desktop;

internal static class VelopackDesktopBootstrap
{
    internal const string AppUserModelId = "clausdk.StorageHub.Desktop";

    public static VelopackApp Build(string[] arguments) =>
        VelopackApp.Build()
            .SetArgs(arguments)
            .SetAppUserModelId(AppUserModelId)
            .OnAfterInstallFastCallback(static _ => CreateHooks().AfterInstall())
            .OnAfterUpdateFastCallback(static _ => CreateHooks().AfterUpdate())
            .OnBeforeUpdateFastCallback(static _ => CreateHooks().BeforeUpdate())
            .OnBeforeUninstallFastCallback(static _ => CreateHooks().BeforeUninstall());

    private static DesktopPackageLifecycleHooks CreateHooks() =>
        new(PackagedDesktopLifecycle.CreateDefault());
}
