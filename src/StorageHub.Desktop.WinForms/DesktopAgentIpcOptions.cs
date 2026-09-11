using StorageHub.Agent.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// Builds the named-pipe client options every desktop agent client shares.
/// The retry envelope is deliberately short: the agent is a local process, so a
/// connect that does not succeed quickly means it is down, and the caller should
/// surface an offline state instead of stalling the UI.
/// </summary>
internal static class DesktopAgentIpcOptions
{
    internal const int ConnectAttempts = 3;

    internal static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(100);

    internal static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromMilliseconds(400);

    // Pinned to this assembly (not the entry assembly) so the reported version stays
    // the desktop build even when a test host or the agent owns the process.
    private static readonly string ClientVersionValue =
        typeof(DesktopAgentIpcOptions).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    internal static NamedPipeIpcClientOptions Create(
        string pipeName,
        string clientName,
        TimeSpan connectTimeout) =>
        new()
        {
            PipeName = pipeName,
            ClientName = clientName,
            ClientVersion = ClientVersionValue,
            ConnectTimeout = connectTimeout,
            MaxConnectAttempts = ConnectAttempts,
            InitialReconnectDelay = InitialReconnectDelay,
            MaximumReconnectDelay = MaximumReconnectDelay
        };
}
