using System.Text.Json;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public enum DesktopAppearance
{
    Light = 1,
    Dark = 2,
    System = 3
}

public enum SshHostKeyDiscoveryMode
{
    Manual = 1,
    AskBeforeFetching = 2,
    Automatic = 3
}

public enum WorkspaceLayout
{
    SideBySide = 1,
    TopAndBottom = 2
}

internal sealed record SshTerminalPreferences(
    string TerminalName = "xterm-256color",
    string? StartupCommand = null,
    int KeepAliveSeconds = 30,
    string FontFamily = "Cascadia Mono",
    float FontSize = 10F,
    int ScrollbackLines = 2_000,
    int RefreshIntervalMilliseconds = 60,
    bool RenderBoldText = true)
{
    internal const int MaximumStartupCommandLength = 512;
    internal static SshTerminalPreferences Defaults { get; } = new();

    internal static SshTerminalPreferences Resolve(SshTerminalPreferences? value)
    {
        value ??= Defaults;
        var terminalName = IsTerminalName(value.TerminalName)
            ? value.TerminalName.Trim()
            : Defaults.TerminalName;
        var startupCommand = string.IsNullOrWhiteSpace(value.StartupCommand)
            ? null
            : IsStartupCommand(value.StartupCommand)
                ? value.StartupCommand.Trim()
                : null;
        var fontFamily = IsFontFamily(value.FontFamily)
            ? value.FontFamily.Trim()
            : Defaults.FontFamily;
        return new SshTerminalPreferences(
            terminalName,
            startupCommand,
            value.KeepAliveSeconds is >= 0 and <= 3_600
                ? value.KeepAliveSeconds : Defaults.KeepAliveSeconds,
            fontFamily,
            float.IsFinite(value.FontSize) && value.FontSize is >= 6F and <= 32F
                ? value.FontSize : Defaults.FontSize,
            value.ScrollbackLines is >= 100 and <= 20_000
                ? value.ScrollbackLines : Defaults.ScrollbackLines,
            value.RefreshIntervalMilliseconds is >= 16 and <= 500
                ? value.RefreshIntervalMilliseconds : Defaults.RefreshIntervalMilliseconds,
            value.RenderBoldText);
    }

    private static bool IsTerminalName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= SshTerminalIpcContract.MaximumTerminalNameLength &&
        !value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character));

    private static bool IsStartupCommand(string value) =>
        value.Length <= MaximumStartupCommandLength &&
        !value.Any(char.IsControl);

    private static bool IsFontFamily(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        !value.Any(char.IsControl);
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
    int MaximumSyncConcurrency = 2,
    DesktopAppearance Appearance = DesktopAppearance.System,
    bool WarnBeforeUnsafeExternalEdit = true,
    IReadOnlyDictionary<string, string>? ConnectionDefaults = null,
    WorkspaceLayout DefaultWorkspaceLayout = WorkspaceLayout.SideBySide,
    SshTerminalPreferences? SshTerminal = null,
    bool ReconnectRemotePanesAutomatically = true,
    bool ConfirmBeforeClearingTransferHistory = true,
    bool ConfirmBeforeDeletingItems = true)
{
    public const int CurrentSchemaVersion = 11;

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
                    concurrencyIsValid ? document.MaximumSyncConcurrency : DesktopUpdatePreferences.Defaults.MaximumSyncConcurrency,
                    document.SchemaVersion >= 5 && Enum.IsDefined(document.Appearance)
                        ? document.Appearance : DesktopAppearance.System,
                    document.WarnBeforeUnsafeExternalEdit,
                    document.SchemaVersion >= 6
                        ? document.ConnectionDefaults is null
                            ? null
                            : ConnectionDefaultSettings.Normalize(document.ConnectionDefaults)
                        : null,
                    document.SchemaVersion >= 7 && Enum.IsDefined(document.DefaultWorkspaceLayout)
                        ? document.DefaultWorkspaceLayout
                        : WorkspaceLayout.SideBySide,
                    document.SchemaVersion >= 8 && document.SshTerminal is not null
                        ? SshTerminalPreferences.Resolve(document.SshTerminal)
                        : null,
                    document.SchemaVersion < 9 || document.ReconnectRemotePanesAutomatically,
                    document.SchemaVersion < 10 || document.ConfirmBeforeClearingTransferHistory,
                    document.SchemaVersion < 11 || document.ConfirmBeforeDeletingItems)
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
            preferences.MinimumConcurrency > preferences.MaximumSyncConcurrency ||
            !Enum.IsDefined(preferences.Appearance))
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
                preferences.MaximumSyncConcurrency,
                preferences.Appearance,
                preferences.WarnBeforeUnsafeExternalEdit,
                preferences.ConnectionDefaults is null
                    ? null
                    : ConnectionDefaultSettings.Normalize(preferences.ConnectionDefaults),
                preferences.DefaultWorkspaceLayout,
                preferences.SshTerminal is null
                    ? null
                    : SshTerminalPreferences.Resolve(preferences.SshTerminal),
                preferences.ReconnectRemotePanesAutomatically,
                preferences.ConfirmBeforeClearingTransferHistory,
                preferences.ConfirmBeforeDeletingItems);
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
        int MaximumSyncConcurrency = 2,
        DesktopAppearance Appearance = DesktopAppearance.System,
        bool WarnBeforeUnsafeExternalEdit = true,
        Dictionary<string, string>? ConnectionDefaults = null,
        WorkspaceLayout DefaultWorkspaceLayout = WorkspaceLayout.SideBySide,
        SshTerminalPreferences? SshTerminal = null,
        bool ReconnectRemotePanesAutomatically = true,
        bool ConfirmBeforeClearingTransferHistory = true,
        bool ConfirmBeforeDeletingItems = true);
}
