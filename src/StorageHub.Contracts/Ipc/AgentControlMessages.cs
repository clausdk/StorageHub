namespace StorageHub.Contracts.Ipc;

public static class AgentControlIpcContract
{
    public const int CurrentVersion = 1;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class AgentControlIpcMessageTypes
{
    public const string ShutdownRequest = "agent.shutdown.request";

    public const string ShutdownResponse = "agent.shutdown.response";
}

public static class AgentShutdownReasons
{
    public const string Update = "update";

    public const string Uninstall = "uninstall";

    public const string Restart = "restart";

    public static bool IsSupported(string reason) =>
        string.Equals(reason, Update, StringComparison.Ordinal) ||
        string.Equals(reason, Uninstall, StringComparison.Ordinal) ||
        string.Equals(reason, Restart, StringComparison.Ordinal);
}

public sealed record AgentShutdownRequest(int ContractVersion, string Reason)
{
    public bool HasValidBounds =>
        AgentControlIpcContract.IsSupported(ContractVersion) &&
        AgentShutdownReasons.IsSupported(Reason);
}

public sealed record AgentShutdownResponse(
    int ContractVersion,
    bool Accepted,
    int ProcessId)
{
    public bool HasValidBounds =>
        AgentControlIpcContract.IsSupported(ContractVersion) &&
        ProcessId > 0;
}
