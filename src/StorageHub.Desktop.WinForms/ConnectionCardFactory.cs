using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

/// <summary>
/// Projects the agent's <see cref="ConnectionSummary"/> into the presentation model the connection
/// list paints.
///
/// Extracted from the Connection Manager dialog when the saved-connection list moved into the shell:
/// two surfaces now render the same connections, and a second copy of this projection would let them
/// disagree about a connection's name, provider or health wording.
/// </summary>
internal static class ConnectionCardFactory
{
    /// <summary>
    /// Shared so the panel's row action and the editor's toolbar warn about the same consequence in
    /// the same words.
    /// </summary>
    internal const string DeleteConfirmationCaption = "Delete saved connection";

    internal static string DeleteConfirmationPrompt(string displayName) =>
        $"Delete '{displayName}'? Queued work will keep its immutable history, but the connection " +
        "can no longer be opened.";

    internal static ConnectionCardModel Create(ConnectionSummary connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var provider = MapProvider(connection.Provider);
        var providerName = ConnectionProviderCatalog.Get(provider).DisplayName;
        var summary = string.IsNullOrWhiteSpace(connection.FolderPath)
            ? $"{providerName} saved profile"
            : $"{providerName} · {connection.FolderPath}";
        return new ConnectionCardModel(
            connection.DisplayName,
            provider,
            summary,
            connection.IsEnabled ? DescribeHealth(connection.Health) : "Disabled",
            connection.IsFavorite,
            connection.ConnectionId,
            connection.IsEnabled,
            connection.AccentColor,
            connection.FolderPath,
            connection.Tags);
    }

    internal static string DescribeHealth(ConnectionHealthSnapshot? health) => health switch
    {
        null => "Not tested",
        { State: ConnectionHealthState.Healthy } => $"Healthy · {health.ElapsedMilliseconds:N0} ms",
        { RequiresCredentialAction: true } => "Credentials need attention",
        { RequiresTrustAction: true } => "Trust decision required",
        { State: ConnectionHealthState.Unavailable } => "Unavailable",
        _ => "Needs attention"
    };

    internal static StorageProviderKind MapProvider(StorageConnectionProvider provider) => provider switch
    {
        StorageConnectionProvider.Local => StorageProviderKind.Local,
        StorageConnectionProvider.S3 => StorageProviderKind.S3,
        StorageConnectionProvider.Ftp => StorageProviderKind.Ftp,
        StorageConnectionProvider.Ftps => StorageProviderKind.Ftps,
        StorageConnectionProvider.Sftp => StorageProviderKind.Sftp,
        StorageConnectionProvider.Ssh => StorageProviderKind.Ssh,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}
