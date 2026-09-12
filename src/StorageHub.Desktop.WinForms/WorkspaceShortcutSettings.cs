namespace StorageHub.Desktop;

/// <summary>
/// A remembered workspace file: where it is, what it was called, and when it was last opened.
/// The name is stored alongside the path so a menu can be drawn without opening every
/// <c>.shw</c> file, and so an entry whose file has gone missing still has a label.
/// </summary>
internal sealed record WorkspaceShortcutEntry(string Path, string? Name, DateTimeOffset LastOpenedUtc)
{
    /// <summary>The label to show, falling back to the file name when no name was recorded.</summary>
    internal string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : Name;
}

/// <summary>
/// A remembered workspace as it is about to be drawn: which list it came from, and whether its
/// file is expected to still be there. The shell decides both, so the views that render it stay
/// free of filesystem access.
/// </summary>
internal sealed record WorkspaceShortcutView(
    WorkspaceShortcutEntry Entry,
    bool IsPinned,
    bool LooksPresent);

internal sealed class WorkspaceShortcutEventArgs(WorkspaceShortcutView shortcut) : EventArgs
{
    internal WorkspaceShortcutView Shortcut { get; } = shortcut;
}

/// <summary>
/// Validates and bounds the pinned and recent workspace lists held in desktop settings.
///
/// Deliberately free of filesystem access and of WinForms. <see cref="DesktopUpdatePreferencesStore.Load"/>
/// runs synchronously on the UI thread from several hot paths, so probing each path here would
/// stall the shell — and against a dead UNC share, stall it for the SMB timeout. Whether a file
/// still exists is decided at render and click time instead.
/// </summary>
internal static class WorkspaceShortcutSettings
{
    internal const int MaximumPinned = 10;

    /// <summary>Matches the recent-connections cap on the Welcome tab.</summary>
    internal const int MaximumRecent = 12;

    internal const int MaximumPathLength = 400;
    internal const int MaximumNameLength = 96;

    /// <summary>
    /// Per-list ceiling on the approximate serialized size. Count caps alone do not bound the
    /// file, because a path at <see cref="MaximumPathLength"/> JSON-escapes to roughly twice its
    /// length — every backslash doubles. Set so that ordinary paths never reach it while a list
    /// of maximum-length paths does, and so that both lists together stay far below the 64 KiB
    /// whole-file limit that <c>Load</c> enforces.
    /// </summary>
    internal const int MaximumListBytes = 8 * 1024;

    internal const string Extension = ".shw";

    /// <summary>
    /// Normalizes stored entries into a list safe to display. Invalid entries are dropped rather
    /// than rejected: a hand-edited or partially corrupt file should degrade to the entries that
    /// still make sense, not discard every setting the user has.
    /// </summary>
    internal static IReadOnlyList<WorkspaceShortcutEntry> Resolve(
        IReadOnlyList<WorkspaceShortcutEntry>? stored,
        int maximum)
    {
        if (stored is null || stored.Count == 0 || maximum <= 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<WorkspaceShortcutEntry>(Math.Min(stored.Count, maximum));
        var budget = 0;
        foreach (var entry in stored)
        {
            if (resolved.Count >= maximum)
            {
                break;
            }

            if (entry is null || !IsValidPath(entry.Path))
            {
                continue;
            }

            // The first occurrence wins: index 0 is the most recent, or the user's chosen order.
            if (!seen.Add(entry.Path))
            {
                continue;
            }

            var name = NormalizeName(entry.Name);
            var cost = EstimateBytes(entry.Path, name);
            if (budget + cost > MaximumListBytes)
            {
                break;
            }

            budget += cost;
            resolved.Add(new WorkspaceShortcutEntry(
                entry.Path,
                name,
                entry.LastOpenedUtc < DateTimeOffset.UnixEpoch ? DateTimeOffset.UnixEpoch : entry.LastOpenedUtc));
        }

        return resolved;
    }

    /// <summary>
    /// Reports why a list cannot be persisted, or null when it is acceptable. Used by the settings
    /// store so an invalid list is refused before anything is written, matching how shortcuts and
    /// concurrency are already validated.
    /// </summary>
    internal static string? Validate(IReadOnlyList<WorkspaceShortcutEntry>? entries, int maximum)
    {
        if (entries is null)
        {
            return null;
        }

        if (entries.Count > maximum)
        {
            return $"At most {maximum} workspace entries can be stored.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var budget = 0;
        foreach (var entry in entries)
        {
            if (entry is null || !IsValidPath(entry.Path))
            {
                return "A workspace entry must be a fully qualified path to a .shw file.";
            }

            if (!seen.Add(entry.Path))
            {
                return $"The workspace '{entry.Path}' is listed more than once.";
            }

            if (entry.Name is { } name && name.Trim().Length > MaximumNameLength)
            {
                return $"A workspace name cannot exceed {MaximumNameLength} characters.";
            }

            budget += EstimateBytes(entry.Path, NormalizeName(entry.Name));
        }

        return budget > MaximumListBytes
            ? "The stored workspace list is too large."
            : null;
    }

    /// <summary>
    /// Moves an entry to the front, refreshing its name and timestamp, without duplicating it.
    /// This is the whole of the most-recently-used behaviour.
    /// </summary>
    internal static IReadOnlyList<WorkspaceShortcutEntry> Promote(
        IReadOnlyList<WorkspaceShortcutEntry>? list,
        WorkspaceShortcutEntry entry,
        int maximum)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var promoted = new List<WorkspaceShortcutEntry> { entry };
        if (list is not null)
        {
            promoted.AddRange(list.Where(existing =>
                existing is not null &&
                !string.Equals(existing.Path, entry.Path, StringComparison.OrdinalIgnoreCase)));
        }

        return Resolve(promoted, maximum);
    }

    internal static IReadOnlyList<WorkspaceShortcutEntry> Remove(
        IReadOnlyList<WorkspaceShortcutEntry>? list,
        string path,
        int maximum)
    {
        if (list is null || list.Count == 0)
        {
            return [];
        }

        return Resolve(
            [.. list.Where(existing =>
                existing is not null &&
                !string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))],
            maximum);
    }

    internal static bool Contains(IReadOnlyList<WorkspaceShortcutEntry>? list, string? path) =>
        path is not null && list is not null &&
        list.Any(entry => entry is not null &&
            string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the file is expected to be there. Only a fixed local drive is probed synchronously:
    /// a probe against a disconnected network share blocks for the SMB timeout, which would hang a
    /// menu as it opens. Anything else is shown optimistically and discovered when it is clicked.
    /// </summary>
    internal static bool LooksPresent(string path)
    {
        if (!IsValidPath(path))
        {
            return false;
        }

        try
        {
            var root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || !System.IO.Path.IsPathRooted(path))
            {
                return true;
            }

            return new System.IO.DriveInfo(root).DriveType != System.IO.DriveType.Fixed ||
                System.IO.File.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            // A malformed or unavailable root is not evidence the workspace is gone.
            return true;
        }
    }

    private static bool IsValidPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Length <= MaximumPathLength &&
        !path.Any(char.IsControl) &&
        System.IO.Path.IsPathFullyQualified(path) &&
        System.IO.Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        if (trimmed.Any(char.IsControl))
        {
            return null;
        }

        return trimmed.Length > MaximumNameLength ? trimmed[..MaximumNameLength] : trimmed;
    }

    /// <summary>
    /// Approximate serialized cost. Paths are doubled because every backslash is escaped in JSON,
    /// and a flat allowance covers the property names, timestamp, and punctuation.
    /// </summary>
    private static int EstimateBytes(string path, string? name) =>
        (path.Length * 2) + (name?.Length ?? 0) + 96;
}
