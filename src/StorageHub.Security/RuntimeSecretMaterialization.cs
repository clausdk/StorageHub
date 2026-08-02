namespace StorageHub.Security;

/// <summary>
/// Materializes a secret into a short-lived, access-restricted file for provider APIs that
/// only accept filesystem paths (for example, SSH private keys and client PFX files).
/// </summary>
public interface IRuntimeSecretFileMaterializer
{
    ValueTask<IRuntimeSecretFile> MaterializeAsync(
        ReadOnlyMemory<byte> secret,
        string fileExtension,
        CancellationToken cancellationToken = default);
}

public interface IRuntimeSecretFile : IAsyncDisposable
{
    string FullPath { get; }
}
