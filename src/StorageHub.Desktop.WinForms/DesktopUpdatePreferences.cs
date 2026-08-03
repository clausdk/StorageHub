using System.Text.Json;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public enum SshHostKeyDiscoveryMode
{
    Manual = 1,
    AskBeforeFetching = 2,
    Automatic = 3
}

internal sealed record DesktopUpdatePreferences(
    bool CheckAutomatically = true,
    bool DownloadAutomatically = true,
    bool RestartAutomatically = false,
    bool IncludePrereleases = true,
    SshHostKeyDiscoveryMode SshHostKeyDiscovery = SshHostKeyDiscoveryMode.AskBeforeFetching,
    string? ExternalEditorPath = null,
    int MaximumEditableFileBytes = EditableFileIpcContract.MaximumContentBytes,
    bool AdaptiveConcurrency = true,
    int MinimumConcurrency = 1,
    int MaximumTransferConcurrency = 4,
    int PerConnectionConcurrency = 2,
    int MaximumSyncConcurrency = 2)
{
    public const int CurrentSchemaVersion = 4;

    public static DesktopUpdatePreferences Defaults { get; } = new();
}

internal sealed class DesktopUpdatePreferencesStore
{
    private const int MaximumSettingsBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _filePath;

    internal DesktopUpdatePreferencesStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException("The desktop settings path must be absolute.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
    }

    internal static DesktopUpdatePreferencesStore CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The current user's local application-data directory is unavailable.");
        }

        return new DesktopUpdatePreferencesStore(
            Path.Combine(localAppData, "StorageHub", "Desktop", "settings.json"));
    }

    internal DesktopUpdatePreferences Load()
    {
        try
        {
            var file = new FileInfo(_filePath);
            if (!file.Exists || file.Length is <= 0 or > MaximumSettingsBytes || IsReparsePoint(file))
            {
                return DesktopUpdatePreferences.Defaults;
            }

            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<DesktopUpdatePreferencesDocument>(stream, JsonOptions);
            if (document is null || document.SchemaVersion is < 1 or > DesktopUpdatePreferences.CurrentSchemaVersion)
            {
                return DesktopUpdatePreferences.Defaults;
            }

            var discoveryMode = document.SchemaVersion == 1
                ? SshHostKeyDiscoveryMode.AskBeforeFetching
                : document.SshHostKeyDiscovery;
            var editorPath = document.SchemaVersion >= 3 && IsValidEditorPath(document.ExternalEditorPath)
                ? document.ExternalEditorPath
                : null;
            var maximumEditableBytes = document.SchemaVersion >= 3 &&
                document.MaximumEditableFileBytes is >= 1 and <= EditableFileIpcContract.MaximumContentBytes
                    ? document.MaximumEditableFileBytes
                    : EditableFileIpcContract.MaximumContentBytes;
            var concurrencyIsValid = document.SchemaVersion >= 4 &&
                document.MinimumConcurrency is >= 1 and <= 8 &&
                document.MaximumTransferConcurrency is >= 1 and <= 32 &&
                document.MinimumConcurrency <= document.MaximumTransferConcurrency &&
                document.PerConnectionConcurrency is >= 1 and <= 16 &&
                document.MaximumSyncConcurrency is >= 1 and <= 8 &&
                document.MinimumConcurrency <= document.MaximumSyncConcurrency;
            return Enum.IsDefined(discoveryMode)
                ? new DesktopUpdatePreferences(
                    document.CheckAutomatically,
                    document.DownloadAutomatically,
                    document.RestartAutomatically,
                    document.IncludePrereleases,
                    discoveryMode,
                    editorPath,
                    maximumEditableBytes,
                    concurrencyIsValid ? document.AdaptiveConcurrency : DesktopUpdatePreferences.Defaults.AdaptiveConcurrency,
                    concurrencyIsValid ? document.MinimumConcurrency : DesktopUpdatePreferences.Defaults.MinimumConcurrency,
                    concurrencyIsValid ? document.MaximumTransferConcurrency : DesktopUpdatePreferences.Defaults.MaximumTransferConcurrency,
                    concurrencyIsValid ? document.PerConnectionConcurrency : DesktopUpdatePreferences.Defaults.PerConnectionConcurrency,
                    concurrencyIsValid ? document.MaximumSyncConcurrency : DesktopUpdatePreferences.Defaults.MaximumSyncConcurrency)
                : DesktopUpdatePreferences.Defaults;
        }
        catch (Exception error) when (error is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException)
        {
            return DesktopUpdatePreferences.Defaults;
        }
    }

    internal void Save(DesktopUpdatePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!IsValidEditorPath(preferences.ExternalEditorPath) ||
            preferences.MaximumEditableFileBytes is < 1 or > EditableFileIpcContract.MaximumContentBytes ||
            preferences.MinimumConcurrency is < 1 or > 8 ||
            preferences.MaximumTransferConcurrency is < 1 or > 32 ||
            preferences.MinimumConcurrency > preferences.MaximumTransferConcurrency ||
            preferences.PerConnectionConcurrency is < 1 or > 16 ||
            preferences.MaximumSyncConcurrency is < 1 or > 8 ||
            preferences.MinimumConcurrency > preferences.MaximumSyncConcurrency)
        {
            throw new ArgumentException("External editor preferences exceed the permitted bounds.", nameof(preferences));
        }

        var parent = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The desktop settings directory is unavailable.");
        Directory.CreateDirectory(parent);
        RejectReparsePoint(_filePath);

        var temporaryPath = Path.Combine(
            parent,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new DesktopUpdatePreferencesDocument(
                DesktopUpdatePreferences.CurrentSchemaVersion,
                preferences.CheckAutomatically,
                preferences.DownloadAutomatically,
                preferences.RestartAutomatically,
                preferences.IncludePrereleases,
                preferences.SshHostKeyDiscovery,
                preferences.ExternalEditorPath,
                preferences.MaximumEditableFileBytes,
                preferences.AdaptiveConcurrency,
                preferences.MinimumConcurrency,
                preferences.MaximumTransferConcurrency,
                preferences.PerConnectionConcurrency,
                preferences.MaximumSyncConcurrency);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            RejectReparsePoint(_filePath);
            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The durable settings file is already committed. A stale uniquely named
                // temporary file contains no secrets and is safe to scavenge later.
            }
        }
    }

    private static bool IsReparsePoint(FileSystemInfo file) =>
        (file.Attributes & FileAttributes.ReparsePoint) != 0;

    private static void RejectReparsePoint(string path)
    {
        if (File.Exists(path) && IsReparsePoint(new FileInfo(path)))
        {
            throw new IOException("Refusing to write desktop settings through a reparse point.");
        }
    }

    private static bool IsValidEditorPath(string? value) => value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 2_048 &&
        !value.Any(char.IsControl) &&
        Path.IsPathFullyQualified(value);

    private sealed record DesktopUpdatePreferencesDocument(
        int SchemaVersion,
        bool CheckAutomatically,
        bool DownloadAutomatically,
        bool RestartAutomatically,
        bool IncludePrereleases,
        SshHostKeyDiscoveryMode SshHostKeyDiscovery = default,
        string? ExternalEditorPath = null,
        int MaximumEditableFileBytes = EditableFileIpcContract.MaximumContentBytes,
        bool AdaptiveConcurrency = true,
        int MinimumConcurrency = 1,
        int MaximumTransferConcurrency = 4,
        int PerConnectionConcurrency = 2,
        int MaximumSyncConcurrency = 2);
}
