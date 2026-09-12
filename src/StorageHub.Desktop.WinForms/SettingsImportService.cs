using StorageHub.Security;

namespace StorageHub.Desktop;

/// <summary>What a file turned out to be, before any password has been asked for.</summary>
internal enum SettingsFileKind
{
    Unreadable = 0,
    PlainText = 1,
    PasswordProtected = 2
}

internal sealed record SettingsFileInspection(
    SettingsFileKind Kind,
    long SizeBytes,
    string? Message);

/// <summary>What applying an import actually did.</summary>
internal sealed record SettingsImportReport(
    IReadOnlySet<SettingsSectionId> Applied,
    IReadOnlyDictionary<SettingsSectionId, string> Blocked,
    string? BackupPath,
    bool ConcurrencyChanged,
    string? Failure,
    SettingsAgentImportResult? Agent = null)
{
    internal bool Succeeded => Failure is null;

    /// <summary>
    /// Connections that arrived without working credentials. Listed so the user is told plainly
    /// rather than discovering it the first time one fails to connect.
    /// </summary>
    internal IReadOnlyList<string> NeedsCredentials =>
        [.. (Agent?.Connections ?? []).Where(item => item.NeedsCredentials).Select(item => item.Name)];
}

/// <summary>
/// Reads an export file and puts it back, one gate at a time.
///
/// The gates run in a deliberate order — size, then shape, then password, then content — so a file
/// that is not an export, or is too big to be one, costs nothing to reject. Nothing is written
/// until the caller has seen what a file contains and confirmed it.
/// </summary>
internal sealed class SettingsImportService
{
    /// <summary>
    /// Kept beside settings.json, in the same per-user directory, and deliberately not password
    /// protected: settings.json is plain there too, so encrypting the safety net would add a
    /// password to lose without moving the boundary it sits inside.
    /// </summary>
    internal const int MaximumBackups = 10;

    private readonly DesktopUpdatePreferencesStore _store;
    private readonly SettingsExportService _exporter;
    private readonly string _backupDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SettingsAgentClients? _agent;

    internal SettingsImportService(
        DesktopUpdatePreferencesStore store,
        SettingsExportService exporter,
        string backupDirectory,
        Func<DateTimeOffset>? clock = null,
        SettingsAgentClients? agent = null)
    {
        _agent = agent;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        _backupDirectory = backupDirectory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal static string DefaultBackupDirectory(string settingsPath) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? Path.GetTempPath(),
        "backups");

    /// <summary>
    /// Decides whether a file is worth asking a password for, reading only its first bytes.
    /// The size gate runs against the directory entry, before anything is opened or allocated.
    /// </summary>
    internal static SettingsFileInspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return Unreadable("That file no longer exists.");
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Unreadable("StorageHub will not read settings through a reparse point.");
            }

            if (file.Length <= 0)
            {
                return Unreadable("That file is empty.");
            }

            if (file.Length > SettingsExportEnvelopeLimits.MaximumFileBytes)
            {
                return Unreadable("That file is too large to be a StorageHub settings export.");
            }

            Span<byte> head = stackalloc byte[8];
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var kind = SettingsExportEnvelope.LooksEncrypted(head[..read])
                ? SettingsFileKind.PasswordProtected
                : SettingsFileKind.PlainText;
            return new SettingsFileInspection(kind, file.Length, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException)
        {
            return Unreadable("That file could not be opened.");
        }
    }

    /// <summary>
    /// Reads a file into a document, decrypting when it is protected. A wrong password and a
    /// damaged file are reported identically, by design.
    /// </summary>
    internal static SettingsExportReadResult Open(string path, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SettingsExportReadResult(
                null, SettingsExportReadFailure.NotAnExport, "That file could not be opened.");
        }

        if (content.Length > SettingsExportEnvelopeLimits.MaximumFileBytes)
        {
            return new SettingsExportReadResult(
                null,
                SettingsExportReadFailure.TooLarge,
                "That file is too large to be a StorageHub settings export.");
        }

        if (!SettingsExportEnvelope.LooksEncrypted(content))
        {
            return SettingsExportSerializer.Read(content);
        }

        if (string.IsNullOrEmpty(password))
        {
            return new SettingsExportReadResult(
                null, SettingsExportReadFailure.NotAnExport, "This file needs a password.");
        }

        try
        {
            return SettingsExportSerializer.Read(SettingsExportEnvelope.Unprotect(content, password));
        }
        catch (SettingsExportCorruptedException error)
        {
            return new SettingsExportReadResult(null, SettingsExportReadFailure.NotAnExport, error.Message);
        }
    }

    /// <summary>
    /// Applies the chosen sections, after saving what is there now.
    ///
    /// The backup is written first and unconditionally: it is the only way back from an import the
    /// user regrets, and producing it with the exporter means the import wizard can read it again.
    ///
    /// Desktop settings are saved last. The agent-backed writes go one item at a time over a pipe
    /// with no transaction spanning them, so if those fail the local settings file is still
    /// untouched and the backup still describes a coherent starting point.
    /// </summary>
    internal async Task<SettingsImportReport> ApplyAsync(
        SettingsExportDocument document,
        IReadOnlyCollection<SettingsSectionId> chosen,
        SettingsConflictPolicy policy = SettingsConflictPolicy.Skip,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(chosen);

        var current = _store.Load();
        var result = DesktopPreferenceSectionMapper.Apply(current, document, chosen);
        var agentSections = chosen
            .Where(id => SettingsSectionCatalog.Get(id).RequiresAgent)
            .Where(id => document.PresentSections().Contains(id))
            .ToArray();
        var hasAgentWork = agentSections.Length > 0 && _agent is not null;

        if (result.Applied.Count == 0 && !hasAgentWork)
        {
            // Nothing to write, so nothing to back up either.
            return new SettingsImportReport(
                result.Applied, result.Blocked, null, ConcurrencyChanged: false, null);
        }

        var backupPath = TryWriteBackup();
        var blocked = new Dictionary<SettingsSectionId, string>(result.Blocked);
        var applied = new HashSet<SettingsSectionId>(result.Applied);
        var agentResult = SettingsAgentImportResult.Empty;

        if (hasAgentWork)
        {
            try
            {
                agentResult = await SettingsAgentTransfer.ApplyAsync(
                    document,
                    agentSections,
                    _agent!,
                    policy,
                    SettingsExportFingerprint.MatchesThisMachine(document.MachineFingerprint),
                    cancellationToken).ConfigureAwait(false);
                foreach (var id in agentSections)
                {
                    _ = applied.Add(id);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or IOException or
                TimeoutException or OperationCanceledException)
            {
                // The agent went away mid-import. Whatever it already wrote stands, and the
                // desktop settings below are still applied, so the user is left with a coherent
                // partial result and a clear reason rather than a silent half-import.
                foreach (var id in agentSections)
                {
                    blocked[id] = $"The background agent could not be reached. {error.Message}";
                }
            }
        }
        else if (agentSections.Length > 0)
        {
            foreach (var id in agentSections)
            {
                blocked[id] = "The background agent is not running.";
            }
        }

        if (result.Applied.Count > 0)
        {
            try
            {
                _store.Save(result.Preferences);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
            {
                // Agent sections that already landed are still reported as applied; only the
                // desktop half failed.
                return new SettingsImportReport(
                    applied.Where(id => SettingsSectionCatalog.Get(id).RequiresAgent).ToHashSet(),
                    blocked,
                    backupPath,
                    ConcurrencyChanged: false,
                    $"StorageHub could not save the imported settings. {error.Message}",
                    agentResult);
            }
        }

        return new SettingsImportReport(
            applied,
            blocked,
            backupPath,
            ConcurrencyDiffers(current, result.Preferences),
            null,
            agentResult);
    }

    /// <summary>
    /// Whether the agent needs restarting for the import to take full effect. It reads these five
    /// values from settings.json once, at startup, so a changed value is inert until then.
    /// </summary>
    internal static bool ConcurrencyDiffers(DesktopUpdatePreferences before, DesktopUpdatePreferences after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.AdaptiveConcurrency != after.AdaptiveConcurrency ||
            before.MinimumConcurrency != after.MinimumConcurrency ||
            before.MaximumTransferConcurrency != after.MaximumTransferConcurrency ||
            before.PerConnectionConcurrency != after.PerConnectionConcurrency ||
            before.MaximumSyncConcurrency != after.MaximumSyncConcurrency;
    }

    /// <summary>
    /// Writes a full unprotected export of the current state. Returns null rather than throwing:
    /// failing to write a safety net is worth reporting, but not worth blocking the import the
    /// user asked for.
    /// </summary>
    internal string? TryWriteBackup()
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            var path = Path.Combine(
                _backupDirectory,
                $"settings-import-{_clock().ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{SettingsExportSerializer.FileExtension}");
            SettingsExportService.Write(
                path,
                _exporter.Capture([.. Enum.GetValues<SettingsSectionId>()]),
                password: null);
            PruneBackups();
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void PruneBackups()
    {
        try
        {
            var stale = new DirectoryInfo(_backupDirectory)
                .GetFiles($"settings-import-*{SettingsExportSerializer.FileExtension}")
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(MaximumBackups);
            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            DirectoryNotFoundException)
        {
            // Keeping one backup too many is harmless.
        }
    }

    private static SettingsFileInspection Unreadable(string message) =>
        new(SettingsFileKind.Unreadable, 0, message);
}
