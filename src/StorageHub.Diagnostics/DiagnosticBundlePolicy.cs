namespace StorageHub.Diagnostics;

public static class DiagnosticBundlePolicy
{
    private static readonly IReadOnlyDictionary<DiagnosticArtifactKind, IReadOnlySet<string>> AllowedArtifacts =
        new Dictionary<DiagnosticArtifactKind, IReadOnlySet<string>>
        {
            [DiagnosticArtifactKind.StructuredLog] = new HashSet<string>(StringComparer.Ordinal)
            {
                "logs/application.log",
                "logs/application.jsonl",
                "logs/agent.log",
                "logs/agent.jsonl"
            },
            [DiagnosticArtifactKind.HealthSnapshot] = new HashSet<string>(StringComparer.Ordinal)
            {
                "health/latest.json"
            },
            [DiagnosticArtifactKind.PackageInventory] = new HashSet<string>(StringComparer.Ordinal)
            {
                "packages/inventory.json"
            },
            [DiagnosticArtifactKind.EnvironmentSummary] = new HashSet<string>(StringComparer.Ordinal)
            {
                "environment/summary.json"
            },
            [DiagnosticArtifactKind.ConfigurationSchema] = new HashSet<string>(StringComparer.Ordinal)
            {
                "configuration/schema.json"
            }
        };

    private static readonly HashSet<string> ForbiddenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".db-wal", ".db-shm", ".vault", ".pfx", ".p12", ".pem", ".key", ".dmp"
    };

    private static readonly string[] ForbiddenNameFragments =
    [
        "credential",
        "secret",
        "private-key",
        "provider-trace",
        "wire-trace",
        "authorization"
    ];

    public static bool IsAllowedRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        if (relativePath.Contains('\\'))
        {
            return false;
        }

        var normalized = relativePath;
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static segment => segment == ".."))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalized);
        if (ForbiddenNameFragments.Any(fragment => fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !ForbiddenExtensions.Contains(Path.GetExtension(fileName)) &&
               AllowedArtifacts.Values.Any(paths => paths.Contains(normalized));
    }

    public static bool IsAllowedArtifact(DiagnosticArtifact candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Length >= 0 &&
               !candidate.ContainsPrivateNames &&
               IsAllowedRelativePath(candidate.RelativePath) &&
               AllowedArtifacts.TryGetValue(candidate.Kind, out var paths) &&
               paths.Contains(candidate.RelativePath);
    }

    public static IReadOnlyList<DiagnosticArtifact> CreateManifest(IEnumerable<DiagnosticArtifact> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(IsAllowedArtifact)
            .GroupBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .OrderBy(static candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }
}
