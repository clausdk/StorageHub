using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using StorageHub.Agent.Ipc;
using StorageHub.Application.Connections;
using StorageHub.Contracts.Ipc;
using StorageHub.Domain.Identifiers;
using StorageHub.Persistence.Connections;
using StorageHub.Security;

namespace StorageHub.Agent.Windows;

public sealed class SshTerminalIpcCommandService : IAgentIpcCommandHandler, IAsyncDisposable
{
    private const int MaximumSessions = 8;
    private static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);
    private readonly IConnectionProfileRepository _profiles;
    private readonly Func<ISecretVault> _getVault;
    private readonly ITrustStore _trustStore;
    private readonly ConcurrentDictionary<Guid, SshTerminalSession> _sessions = new();
    private bool _disposed;

    public SshTerminalIpcCommandService(
        IConnectionProfileRepository profiles,
        Func<ISecretVault> getVault,
        ITrustStore trustStore)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _getVault = getVault ?? throw new ArgumentNullException(nameof(getVault));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
    }

    public bool CanHandle(string messageType) => messageType is
        SshTerminalIpcMessageTypes.OpenRequest or
        SshTerminalIpcMessageTypes.WriteRequest or
        SshTerminalIpcMessageTypes.ReadRequest or
        SshTerminalIpcMessageTypes.ResizeRequest or
        SshTerminalIpcMessageTypes.CloseRequest;

    public async ValueTask<AgentIpcCommandResponse> HandleAsync(
        IpcEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ScavengeIdleSessions();
        return request.MessageType switch
        {
            SshTerminalIpcMessageTypes.OpenRequest => await OpenAsync(request, cancellationToken).ConfigureAwait(false),
            SshTerminalIpcMessageTypes.WriteRequest => await WriteAsync(request, cancellationToken).ConfigureAwait(false),
            SshTerminalIpcMessageTypes.ReadRequest => await ReadAsync(request, cancellationToken).ConfigureAwait(false),
            SshTerminalIpcMessageTypes.ResizeRequest => await ResizeAsync(request, cancellationToken).ConfigureAwait(false),
            SshTerminalIpcMessageTypes.CloseRequest => Close(request),
            _ => AgentIpcCommandResponse.Error("ipc.message.unsupported", "The SSH terminal operation is unsupported.")
        };
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        foreach (var pair in _sessions.ToArray())
        {
            if (_sessions.TryRemove(pair.Key, out var session))
            {
                session.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<AgentIpcCommandResponse> OpenAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        SshTerminalOpenRequest request;
        try
        {
            request = envelope.DeserializePayload<SshTerminalOpenRequest>();
        }
        catch (JsonException)
        {
            return OpenFailure("ssh.terminal.request.invalid", StorageIpcFailureCategory.Validation,
                "The SSH terminal request was invalid.");
        }

        if (!request.HasValidBounds)
        {
            return OpenFailure("ssh.terminal.request.invalid", StorageIpcFailureCategory.Validation,
                "The SSH terminal request was invalid or outside the negotiated bounds.");
        }

        if (_sessions.Count >= MaximumSessions)
        {
            return OpenFailure("ssh.terminal.limit", StorageIpcFailureCategory.Conflict,
                "The maximum number of SSH terminal sessions is already open.");
        }

        try
        {
            var profile = await _profiles.GetAsync(
                new ConnectionProfileId(request.ConnectionId),
                includeDeleted: false,
                cancellationToken).ConfigureAwait(false);
            if (profile is null || !profile.IsEnabled)
            {
                return OpenFailure("ssh.terminal.profile.unavailable", StorageIpcFailureCategory.NotFound,
                    "The SSH client profile was not found or is disabled.");
            }

            if (profile.Endpoint is not SshClientEndpoint endpoint)
            {
                return OpenFailure("ssh.terminal.profile.invalid", StorageIpcFailureCategory.Validation,
                    "The selected connection is not an SSH client profile.");
            }

            var trusted = await GetTrustedFingerprintsAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (trusted.Count == 0)
            {
                return OpenFailure("ssh.terminal.host_key.untrusted", StorageIpcFailureCategory.Security,
                    "The SSH server host key has not been verified for this profile.");
            }

            var session = await CreateSessionAsync(
                profile,
                endpoint,
                trusted,
                request,
                cancellationToken).ConfigureAwait(false);
            var sessionId = Guid.NewGuid();
            if (!_sessions.TryAdd(sessionId, session))
            {
                session.Dispose();
                return OpenFailure("ssh.terminal.unavailable", StorageIpcFailureCategory.Unavailable,
                    "The SSH terminal session could not be registered.", isTransient: true);
            }

            return AgentIpcCommandResponse.Create(
                SshTerminalIpcMessageTypes.OpenResponse,
                new SshTerminalOpenResponse(
                    SshTerminalIpcContract.CurrentVersion,
                    sessionId,
                    profile.Metadata.DisplayName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SshAuthenticationException)
        {
            return OpenFailure("ssh.terminal.authentication_failed", StorageIpcFailureCategory.Unauthorized,
                "SSH authentication failed.");
        }
        catch (SshConnectionException)
        {
            return OpenFailure("ssh.terminal.connection_failed", StorageIpcFailureCategory.Unavailable,
                "The SSH endpoint could not be reached or its host key was rejected.", isTransient: true);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or TimeoutException)
        {
            return OpenFailure("ssh.terminal.unavailable", StorageIpcFailureCategory.Unavailable,
                "The SSH terminal could not be opened.", isTransient: true);
        }
    }

    private async Task<SshTerminalSession> CreateSessionAsync(
        ConnectionProfile profile,
        SshClientEndpoint endpoint,
        HashSet<string> trusted,
        SshTerminalOpenRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticationMethod[] authenticationMethods;
        PrivateKeyFile? authenticationResource = null;
        var vault = _getVault();
        switch (profile.Authentication)
        {
            case UsernamePasswordAuthentication password:
                await using (var lease = await vault.OpenAsync(password.PasswordReference, cancellationToken)
                    .ConfigureAwait(false))
                {
                    authenticationMethods =
                    [
                        new PasswordAuthenticationMethod(
                            password.Username,
                            Encoding.UTF8.GetString(lease.Memory.Span))
                    ];
                }
                break;
            case SftpPrivateKeyAuthentication key:
                await using (var keyLease = await vault.OpenAsync(key.PrivateKeyReference, cancellationToken)
                    .ConfigureAwait(false))
                await using (var passphraseLease = await vault.OpenAsync(
                    key.PassphraseReference ?? throw new InvalidDataException("The SSH private-key passphrase is missing."),
                    cancellationToken).ConfigureAwait(false))
                {
                    var keyBytes = keyLease.Memory.ToArray();
                    try
                    {
                        using var keyStream = new MemoryStream(keyBytes, writable: false);
                        var privateKey = new PrivateKeyFile(
                            keyStream,
                            Encoding.UTF8.GetString(passphraseLease.Memory.Span));
                        authenticationResource = privateKey;
                        authenticationMethods = [new PrivateKeyAuthenticationMethod(key.Username, privateKey)];
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(keyBytes);
                    }
                }
                break;
            case SshPrivateKeyPasswordAuthentication mfa:
                await using (var keyLease = await vault.OpenAsync(mfa.PrivateKeyReference, cancellationToken)
                    .ConfigureAwait(false))
                await using (var passphraseLease = await vault.OpenAsync(mfa.PassphraseReference, cancellationToken)
                    .ConfigureAwait(false))
                await using (var passwordLease = await vault.OpenAsync(mfa.PasswordReference, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var keyBytes = keyLease.Memory.ToArray();
                    try
                    {
                        using var keyStream = new MemoryStream(keyBytes, writable: false);
                        var privateKey = new PrivateKeyFile(
                            keyStream,
                            Encoding.UTF8.GetString(passphraseLease.Memory.Span));
                        authenticationResource = privateKey;
                        authenticationMethods =
                        [
                            new PrivateKeyAuthenticationMethod(mfa.Username, privateKey),
                            new PasswordAuthenticationMethod(
                                mfa.Username,
                                Encoding.UTF8.GetString(passwordLease.Memory.Span))
                        ];
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(keyBytes);
                    }
                }
                break;
            default:
                throw new InvalidDataException("The SSH authentication method is unsupported.");
        }

        SshClient? client = null;
        try
        {
            var connection = new ConnectionInfo(
                endpoint.Host,
                endpoint.Port,
                GetUsername(profile.Authentication),
                authenticationMethods)
            {
                Timeout = profile.OperationalOptions.ConnectTimeout,
                RetryAttempts = Math.Max(1, profile.OperationalOptions.Retry.MaximumAttempts + 1)
            };
            client = new SshClient(connection)
            {
                KeepAliveInterval = request.KeepAliveSeconds == 0
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(request.KeepAliveSeconds)
            };
            client.HostKeyReceived += (_, args) =>
            {
                var fingerprint = $"SHA256:{args.FingerPrintSHA256}";
                args.CanTrust = trusted.Contains(fingerprint);
            };
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var shell = client.CreateShellStream(
                request.TerminalName,
                (uint)request.Columns,
                (uint)request.Rows,
                0,
                0,
                SshTerminalIpcContract.MaximumChunkBytes);
            if (!string.IsNullOrWhiteSpace(request.StartupCommand))
            {
                shell.Write(request.StartupCommand + "\r");
                shell.Flush();
            }
            return new SshTerminalSession(client, shell, authenticationResource);
        }
        catch
        {
            client?.Dispose();
            authenticationResource?.Dispose();
            throw;
        }
    }

    private async Task<HashSet<string>> GetTrustedFingerprintsAsync(
        SshClientEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var records = await _trustStore.FindAsync(
            TrustArtifactKind.SshHostKey,
            endpoint.Host,
            endpoint.Port,
            cancellationToken).ConfigureAwait(false);
        return records
            .Where(record => record.Decision == TrustDecision.Trusted &&
                (record.ExpiresUtc is null || record.ExpiresUtc > now))
            .Select(record => record.Sha256Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
    }

    private async ValueTask<AgentIpcCommandResponse> WriteAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SshTerminalWriteRequest>();
        if (!request.HasValidBounds || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            return WriteFailure(request.SessionId, "ssh.terminal.session.not_found", StorageIpcFailureCategory.NotFound,
                "The SSH terminal session is unavailable.");
        }

        try
        {
            await session.WriteAsync(request.Content, cancellationToken).ConfigureAwait(false);
            return AgentIpcCommandResponse.Create(
                SshTerminalIpcMessageTypes.WriteResponse,
                new SshTerminalWriteResponse(request.ContractVersion, request.SessionId, request.Content.Length));
        }
        catch (Exception error) when (error is IOException or SshConnectionException or InvalidOperationException)
        {
            RemoveSession(request.SessionId);
            return WriteFailure(request.SessionId, "ssh.terminal.disconnected", StorageIpcFailureCategory.Unavailable,
                "The SSH terminal disconnected while sending input.", isTransient: true);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> ReadAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SshTerminalReadRequest>();
        if (!request.HasValidBounds || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            return ReadFailure(request.SessionId, "ssh.terminal.session.not_found", StorageIpcFailureCategory.NotFound,
                "The SSH terminal session is unavailable.");
        }

        try
        {
            var content = await session.ReadAsync(request.MaximumBytes, cancellationToken).ConfigureAwait(false);
            var connected = session.IsConnected;
            if (!connected)
            {
                RemoveSession(request.SessionId);
            }
            return AgentIpcCommandResponse.Create(
                SshTerminalIpcMessageTypes.ReadResponse,
                new SshTerminalReadResponse(request.ContractVersion, request.SessionId, content, connected));
        }
        catch (Exception error) when (error is IOException or SshConnectionException or InvalidOperationException)
        {
            RemoveSession(request.SessionId);
            return ReadFailure(request.SessionId, "ssh.terminal.disconnected", StorageIpcFailureCategory.Unavailable,
                "The SSH terminal disconnected while reading output.", isTransient: true);
        }
    }

    private async ValueTask<AgentIpcCommandResponse> ResizeAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<SshTerminalResizeRequest>();
        if (!request.HasValidBounds || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            return ResizeFailure(request.SessionId, "ssh.terminal.session.not_found", StorageIpcFailureCategory.NotFound,
                "The SSH terminal session is unavailable.");
        }

        try
        {
            await session.ResizeAsync(request.Columns, request.Rows, cancellationToken).ConfigureAwait(false);
            return AgentIpcCommandResponse.Create(
                SshTerminalIpcMessageTypes.ResizeResponse,
                new SshTerminalResizeResponse(request.ContractVersion, request.SessionId, Resized: true));
        }
        catch (Exception error) when (error is IOException or SshConnectionException or InvalidOperationException)
        {
            RemoveSession(request.SessionId);
            return ResizeFailure(request.SessionId, "ssh.terminal.disconnected", StorageIpcFailureCategory.Unavailable,
                "The SSH terminal disconnected while resizing.");
        }
    }

    private AgentIpcCommandResponse Close(IpcEnvelope envelope)
    {
        var request = envelope.DeserializePayload<SshTerminalCloseRequest>();
        var closed = request.HasValidBounds && RemoveSession(request.SessionId);
        return AgentIpcCommandResponse.Create(
            SshTerminalIpcMessageTypes.CloseResponse,
            new SshTerminalCloseResponse(request.ContractVersion, request.SessionId, closed));
    }

    private bool RemoveSession(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }
        session.Dispose();
        return true;
    }

    private void ScavengeIdleSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleLifetime;
        foreach (var pair in _sessions.Where(pair => pair.Value.LastActivityUtc < cutoff).ToArray())
        {
            RemoveSession(pair.Key);
        }
    }

    private static string GetUsername(ConnectionAuthentication authentication) => authentication switch
    {
        UsernamePasswordAuthentication password => password.Username,
        SftpPrivateKeyAuthentication key => key.Username,
        SshPrivateKeyPasswordAuthentication mfa => mfa.Username,
        _ => throw new InvalidDataException("The SSH username is unavailable.")
    };

    private static AgentIpcCommandResponse OpenFailure(string code, StorageIpcFailureCategory category, string message, bool isTransient = false) =>
        AgentIpcCommandResponse.Create(SshTerminalIpcMessageTypes.OpenResponse,
            new SshTerminalOpenResponse(SshTerminalIpcContract.CurrentVersion, Guid.Empty, string.Empty,
                new StorageIpcFailure(code, category, message, isTransient)));

    private static AgentIpcCommandResponse WriteFailure(Guid id, string code, StorageIpcFailureCategory category, string message, bool isTransient = false) =>
        AgentIpcCommandResponse.Create(SshTerminalIpcMessageTypes.WriteResponse,
            new SshTerminalWriteResponse(SshTerminalIpcContract.CurrentVersion, id, 0,
                new StorageIpcFailure(code, category, message, isTransient)));

    private static AgentIpcCommandResponse ReadFailure(Guid id, string code, StorageIpcFailureCategory category, string message, bool isTransient = false) =>
        AgentIpcCommandResponse.Create(SshTerminalIpcMessageTypes.ReadResponse,
            new SshTerminalReadResponse(SshTerminalIpcContract.CurrentVersion, id, [], false,
                new StorageIpcFailure(code, category, message, isTransient)));

    private static AgentIpcCommandResponse ResizeFailure(Guid id, string code, StorageIpcFailureCategory category, string message) =>
        AgentIpcCommandResponse.Create(SshTerminalIpcMessageTypes.ResizeResponse,
            new SshTerminalResizeResponse(SshTerminalIpcContract.CurrentVersion, id, false,
                new StorageIpcFailure(code, category, message, IsTransient: false)));

    private sealed class SshTerminalSession : IDisposable
    {
        private readonly SshClient _client;
        private readonly ShellStream _shell;
        private readonly PrivateKeyFile? _authenticationResource;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        internal SshTerminalSession(SshClient client, ShellStream shell, PrivateKeyFile? authenticationResource)
        {
            _client = client;
            _shell = shell;
            _authenticationResource = authenticationResource;
            LastActivityUtc = DateTimeOffset.UtcNow;
        }

        internal DateTimeOffset LastActivityUtc { get; private set; }
        internal bool IsConnected => !_disposed && _client.IsConnected && _shell.CanRead;

        internal async Task WriteAsync(byte[] content, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await _shell.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await _shell.FlushAsync(cancellationToken).ConfigureAwait(false);
                LastActivityUtc = DateTimeOffset.UtcNow;
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async Task<byte[]> ReadAsync(int maximumBytes, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_shell.DataAvailable)
                {
                    return [];
                }
                var buffer = new byte[maximumBytes];
                var read = await _shell.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                LastActivityUtc = DateTimeOffset.UtcNow;
                return read == buffer.Length ? buffer : buffer[..read];
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
                LastActivityUtc = DateTimeOffset.UtcNow;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _shell.Dispose();
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }
            _client.Dispose();
            _authenticationResource?.Dispose();
            _gate.Dispose();
        }
    }
}
