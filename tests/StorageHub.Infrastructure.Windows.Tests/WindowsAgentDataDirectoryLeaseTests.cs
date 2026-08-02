using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace StorageHub.Infrastructure.Windows.Tests;

public sealed class WindowsAgentDataDirectoryLeaseTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-agent-root-{Guid.NewGuid():N}");

    [Fact]
    public void Acquire_protects_complete_tree_and_holds_per_user_lock()
    {
        var dataRoot = Path.Combine(_testRoot, "data");
        var lockRoot = Path.Combine(_testRoot, "instance");
        var existingDirectory = Path.Combine(dataRoot, "existing");
        var existingFile = Path.Combine(existingDirectory, "state.bin");
        Directory.CreateDirectory(existingDirectory);
        File.WriteAllBytes(existingFile, [1, 2, 3]);

        using var first = WindowsAgentDataDirectoryLease.Acquire(dataRoot, lockRoot);

        Assert.Equal(Path.GetFullPath(dataRoot), first.RootDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(dataRoot), "Agent"), first.AgentDirectory);
        Assert.Equal(Path.Combine(first.AgentDirectory, "CodeLogic"), first.FrameworkDirectory);
        AssertProtectedForCurrentUser(new DirectoryInfo(dataRoot).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access));
        AssertProtectedForCurrentUser(new DirectoryInfo(existingDirectory).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access));
        AssertProtectedForCurrentUser(new FileInfo(existingFile).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access));

        var error = Assert.Throws<WindowsAgentDataDirectoryException>(
            () => WindowsAgentDataDirectoryLease.Acquire(dataRoot, lockRoot));
        Assert.Equal(WindowsAgentDataDirectoryFailure.InUse, error.Failure);
    }

    [Fact]
    public void Different_data_roots_still_share_one_user_instance_lock()
    {
        var lockRoot = Path.Combine(_testRoot, "instance");
        using var first = WindowsAgentDataDirectoryLease.Acquire(
            Path.Combine(_testRoot, "first"),
            lockRoot);

        var error = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
            WindowsAgentDataDirectoryLease.Acquire(Path.Combine(_testRoot, "second"), lockRoot));

        Assert.Equal(WindowsAgentDataDirectoryFailure.InUse, error.Failure);
    }

    [Fact]
    public void Instance_lock_can_live_inside_default_data_root()
    {
        var dataRoot = Path.Combine(_testRoot, "data");
        var lockRoot = Path.Combine(dataRoot, "AgentInstance");

        using var lease = WindowsAgentDataDirectoryLease.Acquire(dataRoot, lockRoot);

        Assert.True(Directory.Exists(lease.FrameworkDirectory));
        AssertProtectedForCurrentUser(new DirectoryInfo(lockRoot).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access));
    }

    [Fact]
    public async Task Lease_can_be_released_on_another_thread_and_reacquired()
    {
        var dataRoot = Path.Combine(_testRoot, "data");
        var lockRoot = Path.Combine(_testRoot, "instance");
        var first = WindowsAgentDataDirectoryLease.Acquire(dataRoot, lockRoot);

        await Task.Run(first.Dispose);
        using var second = WindowsAgentDataDirectoryLease.Acquire(dataRoot, lockRoot);

        Assert.Equal(Path.GetFullPath(dataRoot), second.RootDirectory);
    }

    [Fact]
    public void Relative_unc_and_volume_root_paths_are_rejected()
    {
        var lockRoot = Path.Combine(_testRoot, "instance");
        AssertFailure("relative\\StorageHub", lockRoot, WindowsAgentDataDirectoryFailure.InvalidPath);
        AssertFailure("\\\\server\\share\\StorageHub", lockRoot, WindowsAgentDataDirectoryFailure.RemoteVolume);
        AssertFailure(
            Path.GetPathRoot(Path.GetFullPath(_testRoot))!,
            lockRoot,
            WindowsAgentDataDirectoryFailure.VolumeRoot);
    }

    [Fact]
    public void Reparse_point_in_ancestor_path_is_rejected()
    {
        Directory.CreateDirectory(_testRoot);
        var target = Path.Combine(_testRoot, "target");
        var junction = Path.Combine(_testRoot, "junction");
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);

        try
        {
            var error = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
                WindowsAgentDataDirectoryLease.Acquire(
                    Path.Combine(junction, "StorageHub"),
                    Path.Combine(_testRoot, "instance")));
            Assert.Equal(WindowsAgentDataDirectoryFailure.ReparsePoint, error.Failure);
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }
        }
    }

    [Fact]
    public void Reparse_point_anywhere_below_existing_data_root_is_rejected()
    {
        Directory.CreateDirectory(_testRoot);
        var dataRoot = Path.Combine(_testRoot, "data");
        var target = Path.Combine(_testRoot, "target");
        var junction = Path.Combine(dataRoot, "Agent", "vault");
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);

        try
        {
            var error = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
                WindowsAgentDataDirectoryLease.Acquire(
                    dataRoot,
                    Path.Combine(_testRoot, "instance")));
            Assert.Equal(WindowsAgentDataDirectoryFailure.ReparsePoint, error.Failure);
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static void AssertProtectedForCurrentUser(FileSystemSecurity security)
    {
        var currentUser = WindowsIdentity.GetCurrent().User;
        Assert.Equal(currentUser, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>());
        Assert.Equal(currentUser, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
    }

    private static void AssertFailure(
        string path,
        string lockRoot,
        WindowsAgentDataDirectoryFailure expected)
    {
        var error = Assert.Throws<WindowsAgentDataDirectoryException>(
            () => WindowsAgentDataDirectoryLease.Acquire(path, lockRoot));
        Assert.Equal(expected, error.Failure);
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", junction, target }
        }) ?? throw new InvalidOperationException("Could not start the junction helper.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Could not create test junction. {standardOutput} {standardError}");
    }
}
