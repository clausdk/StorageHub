using System.Collections.ObjectModel;

namespace StorageHub.Sync;

/// <summary>Bounded, provider-neutral filtering applied before planning and baseline comparison.</summary>
public sealed class SyncPathFilterPolicy
{
    public const int MaximumPatternCount = 128;
    public const int MaximumPatternLength = 512;

    public SyncPathFilterPolicy(
        IEnumerable<string>? includeGlobs = null,
        IEnumerable<string>? excludeGlobs = null,
        bool includeHiddenFiles = true)
    {
        IncludeGlobs = Validate(includeGlobs ?? [], nameof(includeGlobs));
        ExcludeGlobs = Validate(excludeGlobs ?? [".storagehub", ".storagehub/**", "**/.storagehub/**"], nameof(excludeGlobs));
        IncludeHiddenFiles = includeHiddenFiles;
    }

    public IReadOnlyList<string> IncludeGlobs { get; }
    public IReadOnlyList<string> ExcludeGlobs { get; }
    public bool IncludeHiddenFiles { get; }

    public bool Includes(string canonicalRelativePath, bool caseSensitive)
    {
        SyncEndpointSnapshot.ValidateRelativePath(canonicalRelativePath, nameof(canonicalRelativePath));
        if (!IncludeHiddenFiles && canonicalRelativePath.Split('/').Any(static part => part.StartsWith('.')))
        {
            return false;
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return (IncludeGlobs.Count == 0 || IncludeGlobs.Any(pattern => GlobMatches(pattern, canonicalRelativePath, comparison))) &&
               !ExcludeGlobs.Any(pattern => GlobMatches(pattern, canonicalRelativePath, comparison));
    }

    private static ReadOnlyCollection<string> Validate(IEnumerable<string> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = source.ToArray();
        if (values.Length > MaximumPatternCount || values.Any(static value =>
                string.IsNullOrWhiteSpace(value) || value.Length > MaximumPatternLength ||
                value.Any(char.IsControl) || value.Contains('\\')))
        {
            throw new ArgumentException("Sync glob filters are invalid or exceed their bounds.", parameterName);
        }

        return new ReadOnlyCollection<string>(values);
    }

    internal static bool GlobMatches(string pattern, string path, StringComparison comparison)
    {
        var memo = new Dictionary<(int Pattern, int Path), bool>();
        return Match(0, 0);

        bool Match(int patternIndex, int pathIndex)
        {
            if (memo.TryGetValue((patternIndex, pathIndex), out var cached))
            {
                return cached;
            }

            bool result;
            if (patternIndex == pattern.Length)
            {
                result = pathIndex == path.Length;
            }
            else if (pattern[patternIndex] == '*')
            {
                var isDouble = patternIndex + 1 < pattern.Length && pattern[patternIndex + 1] == '*';
                var next = patternIndex + (isDouble ? 2 : 1);
                result = Match(next, pathIndex) ||
                         pathIndex < path.Length && (isDouble || path[pathIndex] != '/') && Match(patternIndex, pathIndex + 1);
            }
            else
            {
                result = pathIndex < path.Length &&
                         (pattern[patternIndex] == '?' && path[pathIndex] != '/' ||
                          string.Equals(pattern[patternIndex].ToString(), path[pathIndex].ToString(), comparison)) &&
                         Match(patternIndex + 1, pathIndex + 1);
            }

            memo[(patternIndex, pathIndex)] = result;
            return result;
        }
    }
}
