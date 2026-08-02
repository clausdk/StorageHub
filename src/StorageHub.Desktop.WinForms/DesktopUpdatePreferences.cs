using System.Text.Json;

namespace StorageHub.Desktop;

internal sealed record DesktopUpdatePreferences(
    bool CheckAutomatically = true,
    bool DownloadAutomatically = true,
    bool RestartAutomatically = false,
    bool IncludePrereleases = true)
{
    public const int CurrentSchemaVersion = 1;

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
            return document is { SchemaVersion: DesktopUpdatePreferences.CurrentSchemaVersion }
                ? new DesktopUpdatePreferences(
                    document.CheckAutomatically,
                    document.DownloadAutomatically,
                    document.RestartAutomatically,
                    document.IncludePrereleases)
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
                preferences.IncludePrereleases);
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

    private sealed record DesktopUpdatePreferencesDocument(
        int SchemaVersion,
        bool CheckAutomatically,
        bool DownloadAutomatically,
        bool RestartAutomatically,
        bool IncludePrereleases);
}
