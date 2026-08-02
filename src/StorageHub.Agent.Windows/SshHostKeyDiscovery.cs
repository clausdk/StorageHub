using Renci.SshNet;
using Renci.SshNet.Common;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Agent.Windows;

public sealed record DiscoveredSshHostKey(
    string HostKeyAlgorithm,
    string Sha256Fingerprint);

public interface ISshHostKeyDiscovery
{
    Task<DiscoveredSshHostKey> DiscoverAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default);
}

public sealed class RenciSshHostKeyDiscovery : ISshHostKeyDiscovery
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);

    public async Task<DiscoveredSshHostKey> DiscoverAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        var target = new ConnectionTrustTargetDocument(
            ConnectionTrustArtifactKind.SshHostKey,
            host,
            port);
        if (!target.HasValidBounds)
        {
            throw new ArgumentException("The SSH discovery target is invalid.", nameof(host));
        }

        var authentication = new NoneAuthenticationMethod("storagehub-host-key-discovery");
        var connection = new ConnectionInfo(host, port, authentication.Username, authentication)
        {
            Timeout = DiscoveryTimeout,
            RetryAttempts = 1
        };
        using var client = new SshClient(connection);
        DiscoveredSshHostKey? discovered = null;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            discovered = new DiscoveredSshHostKey(
                eventArgs.HostKeyName,
                $"SHA256:{eventArgs.FingerPrintSHA256}");
            eventArgs.CanTrust = false;
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DiscoveryTimeout);
        try
        {
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (discovered is not null)
        {
            // Discovery deliberately rejects the presented key before authentication.
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (discovered is null ||
            string.IsNullOrWhiteSpace(discovered.HostKeyAlgorithm) ||
            discovered.HostKeyAlgorithm.Length > ConnectionTrustIpcLimits.MaximumHostKeyAlgorithmLength ||
            discovered.HostKeyAlgorithm.Any(static character =>
                char.IsControl(character) || char.IsWhiteSpace(character)) ||
            !ConnectionTrustIpcLimits.IsValidFingerprint(discovered.Sha256Fingerprint))
        {
            throw new InvalidDataException("The SSH endpoint did not present a valid bounded host key.");
        }

        return discovered;
    }
}
