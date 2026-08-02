using CodeLogic.Core.Localization;

namespace StorageHub.Application;

[LocalizationSection("storagehub")]
public sealed class StorageHubStrings : LocalizationModelBase
{
    public string ApplicationName { get; set; } = "StorageHub";

    public string AgentStarting { get; set; } = "Starting StorageHub Agent...";

    public string AgentReady { get; set; } = "StorageHub Agent is ready.";

    public string AgentStopping { get; set; } = "Stopping StorageHub Agent...";

    public string RecoveryOnly { get; set; } = "StorageHub is running in recovery-only mode.";
}
