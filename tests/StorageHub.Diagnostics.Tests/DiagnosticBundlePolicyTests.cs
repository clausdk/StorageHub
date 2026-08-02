namespace StorageHub.Diagnostics.Tests;

public sealed class DiagnosticBundlePolicyTests
{
    [Theory]
    [InlineData("storagehub.db")]
    [InlineData("state/storagehub.db-wal")]
    [InlineData("vault/user.vault")]
    [InlineData("crashes/process.dmp")]
    [InlineData("logs/provider-trace.txt")]
    [InlineData("logs/anything-else.log")]
    [InlineData("logs\\application.log")]
    [InlineData("../outside.log")]
    [InlineData("C:\\secrets\\log.txt")]
    public void RejectsSensitiveOrEscapingArtifacts(string path)
    {
        Assert.False(DiagnosticBundlePolicy.IsAllowedRelativePath(path));
    }

    [Theory]
    [InlineData("logs/application.log")]
    [InlineData("health/latest.json")]
    [InlineData("packages/inventory.json")]
    public void AllowsExplicitSafeArtifactClasses(string path)
    {
        Assert.True(DiagnosticBundlePolicy.IsAllowedRelativePath(path));
    }

    [Fact]
    public void ManifestDropsForbiddenAndNegativeLengthEntries()
    {
        var manifest = DiagnosticBundlePolicy.CreateManifest([
            new DiagnosticArtifact(DiagnosticArtifactKind.StructuredLog, "logs/application.log", 42, false),
            new DiagnosticArtifact(DiagnosticArtifactKind.EnvironmentSummary, "state/storagehub.db", 100, false),
            new DiagnosticArtifact(DiagnosticArtifactKind.HealthSnapshot, "health/latest.json", -1, false)
        ]);

        var only = Assert.Single(manifest);
        Assert.Equal("logs/application.log", only.RelativePath);
    }

    [Fact]
    public void ManifestRequiresThePathToMatchItsDeclaredKind()
    {
        var manifest = DiagnosticBundlePolicy.CreateManifest([
            new DiagnosticArtifact(DiagnosticArtifactKind.PackageInventory, "health/latest.json", 42, false),
            new DiagnosticArtifact(DiagnosticArtifactKind.HealthSnapshot, "health/latest.json", 42, false)
        ]);

        var only = Assert.Single(manifest);
        Assert.Equal(DiagnosticArtifactKind.HealthSnapshot, only.Kind);
    }

    [Fact]
    public void ManifestRejectsArtifactsKnownToContainPrivateNames()
    {
        var artifact = new DiagnosticArtifact(
            DiagnosticArtifactKind.StructuredLog,
            "logs/application.log",
            42,
            ContainsPrivateNames: true);

        Assert.False(DiagnosticBundlePolicy.IsAllowedArtifact(artifact));
        Assert.Empty(DiagnosticBundlePolicy.CreateManifest([artifact]));
    }

    [Fact]
    public void ManifestRejectsDuplicateArchivePaths()
    {
        var artifact = new DiagnosticArtifact(
            DiagnosticArtifactKind.HealthSnapshot,
            "health/latest.json",
            42,
            ContainsPrivateNames: false);

        Assert.Empty(DiagnosticBundlePolicy.CreateManifest([artifact, artifact with { Length = 43 }]));
    }
}
