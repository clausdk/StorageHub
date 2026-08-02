using System.Security.AccessControl;
using System.Security.Principal;

namespace StorageHub.Infrastructure.Windows;

/// <summary>
/// Owns the per-user agent instance lock and protects the complete StorageHub data tree.
/// </summary>
public sealed class WindowsAgentDataDirectoryLease : IDisposable, IAsyncDisposable
{
    private const string LockFileName = ".storagehub-agent.v1.lock";
    private FileStream? _instanceLock;

    private WindowsAgentDataDirectoryLease(string rootDirectory, FileStream instanceLock)
    {
        RootDirectory = rootDirectory;
        AgentDirectory = Path.Combine(rootDirectory, "Agent");
        FrameworkDirectory = Path.Combine(AgentDirectory, "CodeLogic");
        _instanceLock = instanceLock;
    }

    /// <summary>Gets the validated and protected StorageHub data root.</summary>
    public string RootDirectory { get; }

    /// <summary>Gets the protected directory containing agent-owned durable state.</summary>
    public string AgentDirectory { get; }

    /// <summary>Gets the protected CodeLogic discovery directory.</summary>
    public string FrameworkDirectory { get; }

    /// <summary>
    /// Acquires the one-agent-per-Windows-user lock and protects the selected data root. The
    /// optional lock directory exists for isolated tests; production callers must use the fixed
    /// per-user location so different data-root overrides cannot start competing pipe servers.
    /// </summary>
    public static WindowsAgentDataDirectoryLease Acquire(
        string rootDirectory,
        string? instanceLockDirectory = null)
    {
        var fullRootPath = ValidateLocalPath(rootDirectory, "agent data directory");
        var fullInstanceLockDirectory = ValidateLocalPath(
            instanceLockDirectory ?? GetDefaultInstanceLockDirectory(),
            "agent instance lock directory");
        FileStream? instanceLock = null;
        try
        {
            var currentUser = GetCurrentUser();
            instanceLock = AcquireInstanceLock(fullInstanceLockDirectory, currentUser);

            RejectReparsePointsInPath(fullRootPath, "agent data directory");
            Directory.CreateDirectory(fullRootPath);
            ProtectOwnedTree(fullRootPath, currentUser);

            var agentDirectory = Path.Combine(fullRootPath, "Agent");
            var frameworkDirectory = Path.Combine(agentDirectory, "CodeLogic");
            Directory.CreateDirectory(frameworkDirectory);
            ProtectDirectory(agentDirectory, currentUser);
            ProtectDirectory(frameworkDirectory, currentUser);

            var result = new WindowsAgentDataDirectoryLease(fullRootPath, instanceLock);
            instanceLock = null;
            return result;
        }
        catch (WindowsAgentDataDirectoryException)
        {
            throw;
        }
        catch (UnauthorizedAccessException error)
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.AccessDenied,
                "The StorageHub data directory could not be protected for the current user.",
                error);
        }
        catch (IOException error) when (IsSharingViolation(error))
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.InUse,
                "Another StorageHub Agent instance is already running for this Windows user.",
                error);
        }
        catch (IOException error)
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.Unavailable,
                "The StorageHub data directory is unavailable.",
                error);
        }
        catch (Exception error) when (error is System.Security.SecurityException or InvalidOperationException)
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.AccessDenied,
                "The StorageHub data-directory security identity could not be established.",
                error);
        }
        finally
        {
            instanceLock?.Dispose();
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _instanceLock, null)?.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static string GetDefaultInstanceLockDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.InvalidPath,
                "The current user's local application-data directory is unavailable.");
        }

        return Path.Combine(localAppData, "StorageHub", "AgentInstance");
    }

    private static FileStream AcquireInstanceLock(
        string lockDirectory,
        SecurityIdentifier currentUser)
    {
        RejectReparsePointsInPath(lockDirectory, "agent instance lock directory");
        Directory.CreateDirectory(lockDirectory);
        RejectReparsePointsInPath(lockDirectory, "agent instance lock directory");
        ProtectDirectory(lockDirectory, currentUser);

        var lockPath = Path.Combine(lockDirectory, LockFileName);
        RejectReparsePointEntryIfPresent(lockPath, "agent instance lock");
        FileStream? instanceLock = null;
        try
        {
            instanceLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            RejectReparsePointEntryIfPresent(lockPath, "agent instance lock");
            ProtectFile(lockPath, currentUser);
            var attributes = File.GetAttributes(lockPath);
            File.SetAttributes(
                lockPath,
                attributes | FileAttributes.Hidden | FileAttributes.NotContentIndexed);
            var result = instanceLock;
            instanceLock = null;
            return result;
        }
        finally
        {
            instanceLock?.Dispose();
        }
    }

    private static string ValidateLocalPath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.InvalidPath,
                $"The {description} must be an absolute path.");
        }

        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.RemoteVolume,
                $"The {description} must be on a local Windows volume.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.InvalidPath,
                $"The {description} path is invalid.",
                error);
        }

        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.InvalidPath,
                $"The {description} has no volume root.");
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(fullPath),
                Path.TrimEndingDirectorySeparator(volumeRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.VolumeRoot,
                $"A Windows volume root cannot be used as the {description}.");
        }

        DriveType driveType;
        try
        {
            driveType = new DriveInfo(volumeRoot).DriveType;
        }
        catch (Exception error) when (error is ArgumentException or IOException)
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.InvalidPath,
                $"The {description} volume could not be resolved.",
                error);
        }

        if (driveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown)
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.RemoteVolume,
                $"The {description} must be on a local Windows volume.");
        }

        return fullPath;
    }

    private static SecurityIdentifier GetCurrentUser() =>
        WindowsIdentity.GetCurrent().User ??
        throw new InvalidOperationException("The current Windows user SID is unavailable.");

    private static void ProtectOwnedTree(string fullPath, SecurityIdentifier currentUser)
    {
        ProtectDirectory(fullPath, currentUser);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(fullPath);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        while (pendingDirectories.TryPop(out var directoryPath))
        {
            foreach (var entry in new DirectoryInfo(directoryPath)
                .EnumerateFileSystemInfos("*", enumerationOptions))
            {
                var attributes = entry.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WindowsAgentDataDirectoryException(
                        WindowsAgentDataDirectoryFailure.ReparsePoint,
                        "The StorageHub data tree cannot contain reparse points.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ProtectDirectory(entry.FullName, currentUser);
                    pendingDirectories.Push(entry.FullName);
                }
                else
                {
                    ProtectFile(entry.FullName, currentUser);
                }
            }
        }
    }

    private static void RejectReparsePointsInPath(string fullPath, string description)
    {
        for (DirectoryInfo? directory = new(fullPath); directory is not null; directory = directory.Parent)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directory.FullName);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new WindowsAgentDataDirectoryException(
                    WindowsAgentDataDirectoryFailure.ReparsePoint,
                    $"The {description} cannot contain a reparse point in its path.");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new WindowsAgentDataDirectoryException(
                    WindowsAgentDataDirectoryFailure.InvalidPath,
                    $"The {description} path contains a non-directory entry.");
            }
        }
    }

    private static void ProtectDirectory(string fullPath, SecurityIdentifier currentUser)
    {
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        var directory = new DirectoryInfo(fullPath);
        directory.SetAccessControl(security);
        VerifyAccessRules(
            directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
            currentUser,
            "directory");
    }

    private static void ProtectFile(string fullPath, SecurityIdentifier currentUser)
    {
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        var file = new FileInfo(fullPath);
        file.SetAccessControl(security);
        VerifyAccessRules(
            file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access),
            currentUser,
            "file");
    }

    private static void VerifyAccessRules(
        FileSystemSecurity security,
        SecurityIdentifier currentUser,
        string entryKind)
    {
        if (!currentUser.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
            !security.AreAccessRulesProtected)
        {
            throw new IOException($"The StorageHub {entryKind} ownership or inheritance could not be verified.");
        }

        var explicitRules = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (explicitRules.Length != 1 ||
            !currentUser.Equals(explicitRules[0].IdentityReference) ||
            explicitRules[0].AccessControlType != AccessControlType.Allow ||
            (explicitRules[0].FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
        {
            throw new IOException($"The StorageHub {entryKind} access rules could not be verified.");
        }
    }

    private static void RejectReparsePointEntryIfPresent(string path, string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.ReparsePoint,
                $"The {description} cannot be a reparse point.");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new WindowsAgentDataDirectoryException(
                WindowsAgentDataDirectoryFailure.InvalidPath,
                $"The {description} path is occupied by a directory.");
        }
    }

    private static bool IsSharingViolation(IOException error) =>
        (error.HResult & 0xFFFF) is 32 or 33;
}

public enum WindowsAgentDataDirectoryFailure
{
    InvalidPath,
    RemoteVolume,
    VolumeRoot,
    ReparsePoint,
    InUse,
    AccessDenied,
    Unavailable
}

public sealed class WindowsAgentDataDirectoryException : Exception
{
    public WindowsAgentDataDirectoryException(
        WindowsAgentDataDirectoryFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public WindowsAgentDataDirectoryFailure Failure { get; }
}
