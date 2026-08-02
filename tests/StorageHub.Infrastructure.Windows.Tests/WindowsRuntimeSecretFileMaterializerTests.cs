using System.Security.AccessControl;
using System.Security.Principal;

namespace StorageHub.Infrastructure.Windows.Tests;

public sealed class WindowsRuntimeSecretFileMaterializerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-runtime-material-{Guid.NewGuid():N}");

    [Fact]
    public async Task Materialized_file_is_current_user_only_and_deleted_with_its_lease()
    {
        var materializer = new WindowsRuntimeSecretFileMaterializer(_root);
        var secret = "encrypted-private-key"u8.ToArray();

        var material = await materializer.MaterializeAsync(secret, ".key");
        var path = material.FullPath;

        Assert.Equal(secret, await File.ReadAllBytesAsync(path));
        Assert.True((File.GetAttributes(path) & FileAttributes.Hidden) != 0);
        var security = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var currentSid = WindowsIdentity.GetCurrent().User;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        var rule = Assert.Single(rules.Cast<FileSystemAccessRule>());
        Assert.Equal(currentSid, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.True((rule.FileSystemRights & FileSystemRights.FullControl) != 0);

        await material.DisposeAsync();

        Assert.False(File.Exists(path));
        await material.DisposeAsync();
    }

    [Fact]
    public async Task Scavenger_removes_only_aged_runtime_material()
    {
        var materializer = new WindowsRuntimeSecretFileMaterializer(_root);
        var active = await materializer.MaterializeAsync("active"u8.ToArray(), ".pfx");
        var activePath = active.FullPath;
        File.SetCreationTimeUtc(activePath, DateTime.UtcNow.AddDays(-2));
        var orphanPath = Path.Combine(_root, "material-orphan.pfx");
        await File.WriteAllBytesAsync(orphanPath, "orphan"u8.ToArray());
        File.SetCreationTimeUtc(orphanPath, DateTime.UtcNow.AddDays(-2));

        var count = materializer.ScavengeOrphans(TimeSpan.FromHours(24));

        Assert.Equal(1, count);
        Assert.False(File.Exists(orphanPath));
        Assert.True(File.Exists(activePath));
        await active.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_locations_extensions_and_sizes_fail_before_writing()
    {
        Assert.Throws<ArgumentException>(() => new WindowsRuntimeSecretFileMaterializer("relative"));
        Assert.Throws<ArgumentException>(() => new WindowsRuntimeSecretFileMaterializer("\\\\server\\share\\secrets"));
        var materializer = new WindowsRuntimeSecretFileMaterializer(_root);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await materializer.MaterializeAsync(new byte[] { 1 }, ".key.exe"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await materializer.MaterializeAsync(ReadOnlyMemory<byte>.Empty, ".key"));
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
