using System.Security.AccessControl;
using System.Security.Principal;
using StorageHub.Security;

namespace StorageHub.Infrastructure.Windows;

/// <summary>Creates current-user-only runtime secret files and removes them on disposal.</summary>
public sealed class WindowsRuntimeSecretFileMaterializer : IRuntimeSecretFileMaterializer
{
    private const int MaximumSecretLength = 16 * 1024 * 1024;
    private readonly string _rootDirectory;

    public WindowsRuntimeSecretFileMaterializer(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The runtime-secret directory must be an absolute path.", nameof(rootDirectory));
        }

        if (rootDirectory.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("Runtime secrets cannot be materialized on a UNC share.", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public async ValueTask<IRuntimeSecretFile> MaterializeAsync(
        ReadOnlyMemory<byte> secret,
        string fileExtension,
        CancellationToken cancellationToken = default)
    {
        if (secret.IsEmpty || secret.Length > MaximumSecretLength)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "Runtime secret files must contain 1 byte to 16 MiB.");
        }

        var extension = ValidateExtension(fileExtension);
        EnsurePrivateDirectory();
        var path = Path.Combine(_rootDirectory, $"material-{Guid.NewGuid():N}{extension}");
        try
        {
            await using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(secret, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.NotContentIndexed);
            var lifetimeLease = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.None);
            return new RuntimeSecretFile(path, lifetimeLease);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    public int ScavengeOrphans(TimeSpan minimumAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAge, TimeSpan.FromMinutes(1));

        EnsurePrivateDirectory();
        var threshold = DateTime.UtcNow - minimumAge;
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, "material-*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetCreationTimeUtc(path) <= threshold)
                {
                    using (new FileStream(
                               path,
                               FileMode.Open,
                               FileAccess.ReadWrite,
                               FileShare.None,
                               bufferSize: 1,
                               FileOptions.None))
                    {
                    }

                    File.Delete(path);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private void EnsurePrivateDirectory()
    {
        Directory.CreateDirectory(_rootDirectory);
        if ((File.GetAttributes(_rootDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The runtime-secret directory cannot be a reparse point.");
        }

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(_rootDirectory).SetAccessControl(security);
    }

    private static string ValidateExtension(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length is > 12 || value[0] != '.' || !IsAlphanumeric(value.AsSpan(1)))
        {
            throw new ArgumentException("A simple alphanumeric file extension is required.", nameof(value));
        }

        return value.ToLowerInvariant();
    }

    private static bool IsAlphanumeric(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RuntimeSecretFile(string fullPath, FileStream lifetimeLease) : IRuntimeSecretFile
    {
        private string? _fullPath = fullPath;
        private FileStream? _lifetimeLease = lifetimeLease;

        public string FullPath => Volatile.Read(ref _fullPath)
            ?? throw new ObjectDisposedException(nameof(RuntimeSecretFile));

        public ValueTask DisposeAsync()
        {
            var path = Interlocked.Exchange(ref _fullPath, null);
            if (path is not null)
            {
                Interlocked.Exchange(ref _lifetimeLease, null)?.Dispose();
                TryDelete(path);
            }

            return ValueTask.CompletedTask;
        }
    }
}
