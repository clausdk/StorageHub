using CL.Storage;
using CL.Storage.Configuration;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;

namespace StorageHub.Storage.CodeLogic;

/// <summary>
/// Registers secret-bearing CL.Storage configurations only in memory and opens StorageHub sessions.
/// The caller remains responsible for resolving secrets from the vault immediately before this call.
/// </summary>
public sealed class CodeLogicStorageSessionFactory(StorageLibrary library)
{
    private readonly StorageLibrary _library = library ?? throw new ArgumentNullException(nameof(library));

    internal async ValueTask<StorageResult<RuntimeStorageConnection>> RegisterAsync<TConfig>(
        ConnectionProfileId profileId,
        string rootIdentity,
        TConfig configuration,
        CancellationToken cancellationToken = default)
        where TConfig : StorageConnectionConfigBase
        => await RegisterCoreAsync(
            profileId,
            rootIdentity,
            configuration,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<StorageResult<RuntimeStorageConnection>> RegisterLocalAsync(
        ConnectionProfileId profileId,
        string rootIdentity,
        LocalConnectionConfig configuration,
        CancellationToken cancellationToken = default) => await RegisterCoreAsync(
            profileId,
            rootIdentity,
            configuration,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<StorageResult<RuntimeStorageConnection>> RegisterCoreAsync<TConfig>(
        ConnectionProfileId profileId,
        string rootIdentity,
        TConfig configuration,
        CancellationToken cancellationToken,
        IReadOnlyList<IAsyncDisposable>? runtimeResources = null)
        where TConfig : class
    {
        if (profileId.IsEmpty)
        {
            return StorageResult<RuntimeStorageConnection>.Fail(new StorageFailure(
                "storage.profile.invalid",
                StorageFailureKind.Validation,
                "A non-empty connection profile ID is required."));
        }

        if (string.IsNullOrWhiteSpace(rootIdentity))
        {
            return StorageResult<RuntimeStorageConnection>.Fail(new StorageFailure(
                "storage.root.invalid",
                StorageFailureKind.Validation,
                "A root identity is required."));
        }

        ArgumentNullException.ThrowIfNull(configuration);
        var runtimeId = $"storagehub-{profileId.Value:N}-{Guid.NewGuid():N}";
        var registered = false;
        try
        {
            var registration = await _library
                .AddOrUpdateConnectionAsync(runtimeId, configuration, persist: false, cancellationToken)
                .ConfigureAwait(false);
            if (registration.IsFailure)
            {
                return StorageResult<RuntimeStorageConnection>.Fail(CodeLogicStorageMapper.MapFailure(
                    registration.Error,
                    "storage.connection.register_failed",
                    "The runtime storage connection could not be registered."));
            }

            registered = true;

            var storage = _library.GetStorage(runtimeId);
            if (storage.Provider == CL.Storage.Models.StorageProvider.Local)
            {
                await CodeLogicLocalStaging
                    .ScavengeOrphansAsync(storage, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var session = new CodeLogicStorageEndpointSession(
                storage,
                profileId,
                rootIdentity);
            return StorageResult<RuntimeStorageConnection>.Success(
                new RuntimeStorageConnection(_library, runtimeId, session, runtimeResources));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (registered)
            {
                await RemoveRegistrationBestEffortAsync(runtimeId).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception)
        {
            if (registered)
            {
                await RemoveRegistrationBestEffortAsync(runtimeId).ConfigureAwait(false);
            }

            return StorageResult<RuntimeStorageConnection>.Fail(CodeLogicStorageMapper.Unexpected("connection registration"));
        }
    }

    private async ValueTask RemoveRegistrationBestEffortAsync(string runtimeId)
    {
        try
        {
            await _library.RemoveConnectionAsync(runtimeId, persist: false).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Preserve the original registration failure. The CodeLogic host owns final cleanup.
        }
    }

    internal ValueTask<StorageResult<RuntimeStorageConnection>> RegisterPreparedAsync(
        ConnectionProfileId profileId,
        string rootIdentity,
        object configuration,
        IReadOnlyList<IAsyncDisposable> runtimeResources,
        CancellationToken cancellationToken) => configuration switch
        {
            LocalConnectionConfig local => RegisterCoreAsync(
                profileId, rootIdentity, local, cancellationToken, runtimeResources),
            S3ConnectionConfig s3 => RegisterCoreAsync(
                profileId, rootIdentity, s3, cancellationToken, runtimeResources),
            FtpConnectionConfig ftp => RegisterCoreAsync(
                profileId, rootIdentity, ftp, cancellationToken, runtimeResources),
            SftpConnectionConfig sftp => RegisterCoreAsync(
                profileId, rootIdentity, sftp, cancellationToken, runtimeResources),
            _ => ValueTask.FromResult(StorageResult<RuntimeStorageConnection>.Fail(new StorageFailure(
                "storage.profile.unsupported",
                StorageFailureKind.Unsupported,
                "The prepared connection type is unsupported by CL.Storage.")))
        };
}

public sealed class RuntimeStorageConnection : IAsyncDisposable
{
    private readonly StorageLibrary _library;
    private int _disposed;

    internal RuntimeStorageConnection(
        StorageLibrary library,
        string runtimeConnectionId,
        CodeLogicStorageEndpointSession session,
        IReadOnlyList<IAsyncDisposable>? runtimeResources = null)
    {
        _library = library;
        RuntimeConnectionId = runtimeConnectionId;
        Session = session;
        RuntimeResources = runtimeResources ?? [];
    }

    public string RuntimeConnectionId { get; }

    public CodeLogicStorageEndpointSession Session { get; }

    private IReadOnlyList<IAsyncDisposable> RuntimeResources { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Session.DisposeAsync().ConfigureAwait(false);
            try
            {
                await _library.RemoveConnectionAsync(RuntimeConnectionId, persist: false).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The CodeLogic host may already be stopping; its lifecycle owns final backend disposal.
            }
        }
        finally
        {
            for (var index = RuntimeResources.Count - 1; index >= 0; index--)
            {
                await RuntimeResources[index].DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
