using System.Security.Cryptography;
using System.Security.Principal;

namespace StorageHub.Agent.Ipc;

/// <summary>Builds bounded, per-account names for StorageHub's current-user-only pipes.</summary>
public static class StorageHubIpcPipeNames
{
    private const int AccountHashLengthBytes = 16;
    private const string NormalPrefix = "StorageHub.Agent.v1.user-";
    private const string SecretPrefix = "StorageHub.Agent.Secrets.v1.user-";
    private static readonly Lazy<(string Normal, string Secret)> CurrentNames = new(CreateCurrentNames);

    public static string Normal => CurrentNames.Value.Normal;

    public static string Secret => CurrentNames.Value.Secret;

    private static (string Normal, string Secret) CreateCurrentNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "StorageHub per-account pipe names require a Windows account SID.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var accountSid = identity.User ??
            throw new InvalidOperationException("The current Windows account SID is unavailable.");
        var sidBytes = new byte[accountSid.BinaryLength];
        accountSid.GetBinaryForm(sidBytes, 0);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(sidBytes, digest);
        var accountHash = Convert.ToHexStringLower(digest[..AccountHashLengthBytes]);
        return (NormalPrefix + accountHash, SecretPrefix + accountHash);
    }
}
