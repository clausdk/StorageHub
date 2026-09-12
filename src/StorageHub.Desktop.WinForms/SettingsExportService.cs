using StorageHub.Security;

namespace StorageHub.Desktop;

/// <summary>
/// Builds export documents and writes them to disk, with or without a password.
///
/// Desktop sections are read straight from the settings store; the ones the agent owns come from
/// <see cref="SettingsAgentTransfer"/> over the pipe, which is why capturing everything is async.
/// </summary>
internal sealed class SettingsExportService
{
    private readonly DesktopUpdatePreferencesStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _application;
    private readonly Func<string?> _fingerprint;
    private readonly SettingsAgentClients? _agent;

    internal SettingsExportService(
        DesktopUpdatePreferencesStore store,
        SettingsAgentClients? agent = null,
        Func<DateTimeOffset>? clock = null,
        Func<string>? application = null,
        Func<string?>? fingerprint = null)
    {
        _agent = agent;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _application = application ?? (() => $"StorageHub {DesktopApplicationVersion.Current}");
        _fingerprint = fingerprint ?? SettingsExportFingerprint.Compute;
    }

    /// <summary>
    /// Captures the chosen desktop sections. Dependencies are expanded first, so a file can never
    /// describe something it does not also carry what is needed to rebuild.
    ///
    /// Desktop sections only; <see cref="CaptureAsync"/> adds the ones the agent owns.
    /// </summary>
    internal SettingsExportDocument Capture(IReadOnlyCollection<SettingsSectionId> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var selected = SettingsSectionCatalog.ExpandForExport(chosen);
        var preferences = _store.Load();
        var document = SettingsExportSerializer.Create(_clock(), _application(), _fingerprint());

        if (selected.Contains(SettingsSectionId.DesktopGeneral))
        {
            document = document with
            {
                DesktopGeneral = DesktopPreferenceSectionMapper.CaptureGeneral(preferences)
            };
        }

        if (selected.Contains(SettingsSectionId.Shortcuts))
        {
            document = document with { Shortcuts = ShortcutSettings.Resolve(preferences.Shortcuts) };
        }

        if (selected.Contains(SettingsSectionId.ConnectionDefaults))
        {
            document = document with
            {
                ConnectionDefaults = DesktopPreferenceSectionMapper.CaptureConnectionDefaults(preferences)
            };
        }

        if (selected.Contains(SettingsSectionId.MachineSpecific))
        {
            document = document with
            {
                MachineSpecific = DesktopPreferenceSectionMapper.CaptureMachineSpecific(preferences)
            };
        }

        return document;
    }

    /// <summary>
    /// Captures everything chosen, including the sections the agent owns.
    ///
    /// Agent sections are only reachable when a client bundle was supplied, so a shell running
    /// without the agent exports what it can rather than failing outright.
    /// </summary>
    internal async Task<SettingsExportDocument> CaptureAsync(
        IReadOnlyCollection<SettingsSectionId> chosen,
        CancellationToken cancellationToken = default)
    {
        var document = Capture(chosen);
        if (_agent is null) return document;

        return await SettingsAgentTransfer.CaptureAsync(
            document,
            SettingsSectionCatalog.ExpandForExport(chosen),
            _agent,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the agent-backed sections can be read at all.</summary>
    internal bool CanReachAgent => _agent is not null;

    /// <summary>
    /// Writes a document to <paramref name="path"/>, sealed when a password is given.
    ///
    /// Written the same way the settings file is: to a uniquely named temporary file beside the
    /// destination, flushed to disk, then moved into place, so a failure midway cannot leave a
    /// half-written export that would later read as corrupt.
    /// </summary>
    internal static void Write(string path, SettingsExportDocument document, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The export path must be absolute.", nameof(path));
        }

        var payload = SettingsExportSerializer.Serialize(document);
        var content = string.IsNullOrEmpty(password)
            ? payload
            : SettingsExportEnvelope.Protect(payload, password);
        if (content.Length > SettingsExportEnvelopeLimits.MaximumFileBytes)
        {
            throw new ArgumentException("These settings are too large to export.", nameof(document));
        }

        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
            ?? throw new IOException("The export directory is unavailable.");
        Directory.CreateDirectory(parent);
        RejectReparsePoint(full);

        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            RejectReparsePoint(full);
            if (File.Exists(full))
            {
                File.Replace(temporaryPath, full, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, full);
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
                // The export is already committed; a stale uniquely named temporary file is safe
                // to leave for later scavenging.
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.Exists(path) && (new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to write the export through a reparse point.");
        }
    }
}
