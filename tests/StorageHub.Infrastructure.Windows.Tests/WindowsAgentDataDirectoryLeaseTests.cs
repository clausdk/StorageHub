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
    public void Acquire_protects_agent_tree_without_rewriting_siblings_and_holds_per_user_lock()
    {
        var dataRoot = Path.Combine(_testRoot, "data");
        var lockRoot = Path.Combine(_testRoot, "instance");
        var existingDirectory = Path.Combine(dataRoot, "Agent", "existing");
        var existingFile = Path.Combine(existingDirectory, "state.bin");
        var desktopCache = Path.Combine(dataRoot, "Desktop", "listing-cache.db");
        Directory.CreateDirectory(existingDirectory);
        File.WriteAllBytes(existingFile, [1, 2, 3]);
        Directory.CreateDirectory(Path.GetDirectoryName(desktopCache)!);
        File.WriteAllBytes(desktopCache, [4, 5, 6]);
        var siblingSecurityBefore = new FileInfo(desktopCache).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);

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
        var siblingSecurityAfter = new FileInfo(desktopCache).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Equal(siblingSecurityBefore.AreAccessRulesProtected, siblingSecurityAfter.AreAccessRulesProtected);

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
    public void Data_and_application_trees_must_be_disjoint()
    {
        var applicationRoot = Path.Combine(_testRoot, "StorageHub.Desktop");
        var siblingDataRoot = Path.Combine(_testRoot, "StorageHub");

        WindowsAgentDataDirectoryLease.EnsureDataRootIsSeparateFromApplication(
            siblingDataRoot,
            applicationRoot);

        _ = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
            WindowsAgentDataDirectoryLease.EnsureDataRootIsSeparateFromApplication(
                applicationRoot,
                applicationRoot));
        _ = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
            WindowsAgentDataDirectoryLease.EnsureDataRootIsSeparateFromApplication(
                Path.Combine(applicationRoot, "Data"),
                applicationRoot));
        _ = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
            WindowsAgentDataDirectoryLease.EnsureDataRootIsSeparateFromApplication(
                _testRoot,
                applicationRoot));
    }

    [Fact]
    public void Packaged_agent_resolves_the_complete_velopack_owned_tree()
    {
        var applicationRoot = Path.Combine(_testRoot, "StorageHub.Desktop");
        var agentDirectory = Path.Combine(applicationRoot, "current", "Agent");

        var resolvedRoot = WindowsAgentDataDirectoryLease.ResolveApplicationOwnedTreeRoot(
            agentDirectory);

        Assert.Equal(Path.GetFullPath(applicationRoot), resolvedRoot);
        _ = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
            WindowsAgentDataDirectoryLease.EnsureDataRootIsSeparateFromApplication(
                Path.Combine(applicationRoot, "Data"),
                resolvedRoot));
        _ = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
            WindowsAgentDataDirectoryLease.EnsureDataRootIsSeparateFromApplication(
                Path.Combine(applicationRoot, "current", "Data"),
                resolvedRoot));
    }

    [Fact]
    public void Non_packaged_agent_keeps_its_exact_application_directory()
    {
        var applicationDirectory = Path.Combine(_testRoot, "bin", "Release", "net10.0-windows");

        var resolvedRoot = WindowsAgentDataDirectoryLease.ResolveApplicationOwnedTreeRoot(
            applicationDirectory);

        Assert.Equal(Path.GetFullPath(applicationDirectory), resolvedRoot);
    }

    [Fact]
    public void Application_tree_must_not_contain_the_fixed_instance_lock()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultDataRoot = Path.Combine(localAppData, "StorageHub");

        _ = Assert.Throws<WindowsAgentDataDirectoryException>(() =>
            WindowsAgentDataDirectoryLease.EnsureApplicationTreeIsSeparateFromInstanceLock(
                defaultDataRoot));
        WindowsAgentDataDirectoryLease.EnsureApplicationTreeIsSeparateFromInstanceLock(
            Path.Combine(localAppData, "StorageHub.Desktop"));
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

    [Fact]
    public void Reparse_point_below_sibling_data_does_not_block_agent_lease()
    {
        Directory.CreateDirectory(_testRoot);
        var dataRoot = Path.Combine(_testRoot, "data");
        var target = Path.Combine(_testRoot, "target");
        var junction = Path.Combine(dataRoot, "Desktop", "external-cache");
        Directory.CreateDirectory(Path.GetDirectoryName(junction)!);
        Directory.CreateDirectory(target);
        CreateJunction(junction, target);

        try
        {
            using var lease = WindowsAgentDataDirectoryLease.Acquire(
                dataRoot,
                Path.Combine(_testRoot, "instance"));

            Assert.True(Directory.Exists(lease.FrameworkDirectory));
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
