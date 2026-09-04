using System.Text;
using System.Text.Json;
using StorageHub.Agent.Ipc;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Security;

namespace StorageHub.Agent.Windows.Tests;

public sealed class SshTerminalIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"storagehub-ssh-terminal-{Guid.NewGuid():N}");

    [Fact]
    [Trait("Category", "SshTerminalIntegration")]
    public async Task ManagedClientOpensWritesReadsResizesAndClosesAgainstLoopbackServer()
    {
        var portValue = Environment.GetEnvironmentVariable("STORAGEHUB_SFTP_PASSWORD_PORT");
        var password = Environment.GetEnvironmentVariable("STORAGEHUB_SFTP_PASSWORD");
        var username = Environment.GetEnvironmentVariable("STORAGEHUB_SFTP_USERNAME");
        var fingerprintHex = Environment.GetEnvironmentVariable("STORAGEHUB_SFTP_HOST_SHA256");
        var privateKeyPath = Environment.GetEnvironmentVariable("STORAGEHUB_SFTP_CLIENT_KEY_PATH");
        var privateKeyPassphrase = Environment.GetEnvironmentVariable("STORAGEHUB_SFTP_CLIENT_KEY_PASSPHRASE");
        var required = string.Equals(
            Environment.GetEnvironmentVariable("STORAGEHUB_REQUIRE_SFTP"), "1", StringComparison.Ordinal);
        if (!int.TryParse(portValue, out var port) || string.IsNullOrEmpty(password) ||
            string.IsNullOrEmpty(username) || string.IsNullOrEmpty(fingerprintHex) ||
            string.IsNullOrEmpty(privateKeyPath) || !File.Exists(privateKeyPath) ||
            string.IsNullOrEmpty(privateKeyPassphrase))
        {
            if (required)
            {
                throw new InvalidOperationException("The required SSH terminal fixture is incomplete.");
            }
            return;
        }

        Directory.CreateDirectory(_directory);
        using var vault = new VersionedFileSecretVault(
            Path.Combine(_directory, "vault"), new TestSecretProtector());
        var passwordReference = await vault.CreateAsync(Encoding.UTF8.GetBytes(password));
        var privateKeyReference = await vault.CreateAsync(await File.ReadAllBytesAsync(privateKeyPath));
        var passphraseReference = await vault.CreateAsync(Encoding.UTF8.GetBytes(privateKeyPassphrase));
        var profile = ConnectionProfile.Create(
            ConnectionProfileId.New(),
            new ConnectionProfileMetadata("Loopback SSH"),
            new SshClientEndpoint("127.0.0.1", port, SshHostKeyPolicy.Pinned),
            new SshPrivateKeyPasswordAuthentication(
                username,
                passwordReference.Reference,
                privateKeyReference.Reference,
                passphraseReference.Reference,
                SftpPrivateKeyFormat.OpenSsh),
            Options(),
            DateTimeOffset.UtcNow);
        var fingerprint = "SHA256:" + Convert.ToBase64String(
            Convert.FromHexString(fingerprintHex)).TrimEnd('=');
        var trust = new StaticTrustStore(new TrustRecord(
            Guid.NewGuid().ToString("N"),
            TrustArtifactKind.SshHostKey,
            "127.0.0.1",
            port,
            "ssh",
            fingerprint,
            TrustDecision.Trusted,
            TrustDecisionSource.UserVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            1));
        await using var service = new SshTerminalIpcCommandService(
            new StaticProfileRepository(profile), () => vault, trust);

        var startupMarker = $"startup-{Guid.NewGuid():N}";
        var opened = await SendAsync<SshTerminalOpenRequest, SshTerminalOpenResponse>(
            service,
            SshTerminalIpcMessageTypes.OpenRequest,
            new SshTerminalOpenRequest(
                SshTerminalIpcContract.CurrentVersion,
                profile.Id.Value,
                100,
                30,
                "screen-256color",
                $"printf '{startupMarker}\\n'",
                KeepAliveSeconds: 1));
        Assert.Null(opened.Failure);
        Assert.NotEqual(Guid.Empty, opened.SessionId);

        var startupOutput = await ReadUntilAsync(service, opened.SessionId, startupMarker);
        Assert.Contains(startupMarker, startupOutput, StringComparison.Ordinal);

        var resized = await SendAsync<SshTerminalResizeRequest, SshTerminalResizeResponse>(
            service,
            SshTerminalIpcMessageTypes.ResizeRequest,
            new SshTerminalResizeRequest(SshTerminalIpcContract.CurrentVersion, opened.SessionId, 120, 40));
        Assert.True(resized.Resized);

        var marker = $"storagehub-{Guid.NewGuid():N}";
        var written = await SendAsync<SshTerminalWriteRequest, SshTerminalWriteResponse>(
            service,
            SshTerminalIpcMessageTypes.WriteRequest,
            new SshTerminalWriteRequest(
                SshTerminalIpcContract.CurrentVersion,
                opened.SessionId,
                Encoding.UTF8.GetBytes(marker + "\r")));
        Assert.Null(written.Failure);
        Assert.Equal(marker.Length + 1, written.AcceptedBytes);

        var output = await ReadUntilAsync(service, opened.SessionId, marker);
        Assert.Contains(marker, output, StringComparison.Ordinal);

        var closed = await SendAsync<SshTerminalCloseRequest, SshTerminalCloseResponse>(
            service,
            SshTerminalIpcMessageTypes.CloseRequest,
            new SshTerminalCloseRequest(SshTerminalIpcContract.CurrentVersion, opened.SessionId));
        Assert.True(closed.Closed);
    }

    private static async Task<string> ReadUntilAsync(
        SshTerminalIpcCommandService service,
        Guid sessionId,
        string marker)
    {
        var output = new StringBuilder();
        for (var attempt = 0; attempt < 100 && !output.ToString().Contains(marker, StringComparison.Ordinal); attempt++)
        {
            var read = await SendAsync<SshTerminalReadRequest, SshTerminalReadResponse>(
                service,
                SshTerminalIpcMessageTypes.ReadRequest,
                new SshTerminalReadRequest(
                    SshTerminalIpcContract.CurrentVersion,
                    sessionId,
                    SshTerminalIpcContract.MaximumChunkBytes));
            Assert.Null(read.Failure);
            output.Append(Encoding.UTF8.GetString(read.Content));
            if (read.Content.Length == 0)
            {
                await Task.Delay(20);
            }
        }

        return output.ToString();
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        SshTerminalIpcCommandService service,
        string messageType,
        TRequest payload)
        where TRequest : class
        where TResponse : class
    {
        var response = await service.HandleAsync(IpcEnvelope.Create(messageType, Guid.NewGuid(), 1, payload));
        return response.Payload.Deserialize<TResponse>() ??
            throw new InvalidDataException("The SSH terminal response was empty.");
    }

    private static ConnectionOperationalOptions Options() => new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        new ConnectionRetryPolicy(0, TimeSpan.Zero, TimeSpan.Zero),
        null,
        new ConnectionBandwidthLimits(null, null),
        "utf-8");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Scheme => "test-copy-v1";
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) => plaintext.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> entropy) => protectedData.ToArray();
    }

    private sealed class StaticProfileRepository(ConnectionProfile profile) : IConnectionProfileRepository
    {
        public ValueTask<ConnectionProfile?> GetAsync(ConnectionProfileId id, bool includeDeleted = false,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ConnectionProfile?>(id == profile.Id ? profile : null);

        public ValueTask<ConnectionProfileWriteResult> CreateAsync(ConnectionProfile value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ConnectionProfile>> SearchAsync(ConnectionProfileSearch search,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ConnectionProfileWriteResult> UpdateAsync(ConnectionProfile value, long expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ConnectionProfileWriteResult> SetEnabledAsync(ConnectionProfileId id, bool enabled,
            long expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ConnectionProfileWriteResult> SoftDeleteAsync(ConnectionProfileId id, long expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StaticTrustStore(TrustRecord record) : ITrustStore
    {
        public ValueTask<IReadOnlyList<TrustRecord>> FindAsync(TrustArtifactKind artifactKind, string canonicalHost,
            int port, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TrustRecord>>(
                artifactKind == record.ArtifactKind && canonicalHost == record.CanonicalHost && port == record.Port
                    ? [record]
                    : []);

        public ValueTask UpsertAsync(TrustRecord value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<bool> RemoveAsync(string trustId, int expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
