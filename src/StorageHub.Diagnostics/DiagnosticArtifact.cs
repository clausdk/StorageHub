namespace StorageHub.Diagnostics;

public enum DiagnosticArtifactKind
{
    StructuredLog,
    HealthSnapshot,
    PackageInventory,
    EnvironmentSummary,
    ConfigurationSchema
}

public sealed record DiagnosticArtifact(
    DiagnosticArtifactKind Kind,
    string RelativePath,
    long Length,
    bool ContainsPrivateNames);
