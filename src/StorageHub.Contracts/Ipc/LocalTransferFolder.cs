using System.Security.Cryptography;
using System.Text;

namespace StorageHub.Contracts.Ipc;

/// <summary>
/// The wire encoding for a local folder used as one side of a queued transfer.
///
/// A queued endpoint is normally a saved profile the agent resolves for itself, so there is no
/// profile to name when the endpoint is a folder on this PC. The folder's canonical path is
/// carried in the root identity instead, and the connection id is derived from it so the pair
/// cannot be recombined into an address that was never issued.
///
/// This type performs no validation beyond shaping the identity: the agent re-derives and
/// re-approves the folder on every enqueue and every open, because a path chosen in the UI is a
/// request rather than evidence.
/// </summary>
public static class LocalTransferFolder
{
    private const string Prefix = "localuser:v1:";
    private static readonly Guid Namespace = new("9D2C2F04-6E1B-4B87-9E5B-2F1A83C6D4A7");

    public static bool IsLocalFolder(string? rootIdentity) =>
        rootIdentity?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Encodes a canonical folder path as a transfer root identity.</summary>
    public static string CreateRootIdentity(string canonicalFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalFolder);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(Normalize(canonicalFolder)));
    }

    /// <summary>The connection id that belongs to a folder's root identity.</summary>
    public static Guid CreateConnectionId(string canonicalFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalFolder);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            Namespace + "|" + Normalize(canonicalFolder).ToUpperInvariant()));
        return new Guid(hash[..16]);
    }

    /// <summary>Recovers the folder path from a root identity, or null when it is not one.</summary>
    public static string? TryReadFolder(string? rootIdentity)
    {
        if (!IsLocalFolder(rootIdentity))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(rootIdentity![Prefix.Length..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Normalize(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
