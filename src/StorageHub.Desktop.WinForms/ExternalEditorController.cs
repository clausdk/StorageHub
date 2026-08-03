using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

internal sealed class ExternalEditorController : IAsyncDisposable
{
    private readonly DesktopUpdatePreferencesStore _preferencesStore;
    private readonly NamedPipeObjectInspectorAgentClient _client;
    private readonly List<ExternalEditSession> _sessions = [];
    private bool _disposed;

    internal ExternalEditorController(DesktopUpdatePreferencesStore preferencesStore)
    {
        _preferencesStore = preferencesStore;
        _client = new NamedPipeObjectInspectorAgentClient();
        ScavengeStaleSessions();
    }

    internal event EventHandler? FileUploaded;

    internal async Task OpenAsync(
        IWin32Window owner,
        ObjectInspectorAddress address,
        string fileName,
        long? capturedLength,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var preferences = _preferencesStore.Load();
        var maximumBytes = Math.Clamp(
            preferences.MaximumEditableFileBytes,
            1,
            EditableFileIpcContract.MaximumContentBytes);
        if (capturedLength is > 0 && capturedLength > maximumBytes)
        {
            Show(owner, $"'{fileName}' is larger than the configured {FormatLimit(maximumBytes)} editing limit.", MessageBoxIcon.Warning);
            return;
        }

        var response = await _client.DownloadEditableFileAsync(new EditableFileDownloadRequest(
            EditableFileIpcContract.CurrentVersion,
            address,
            maximumBytes), cancellationToken).ConfigureAwait(true);
        if (response.Failure is not null)
        {
            Show(owner, response.Failure.Message, MessageBoxIcon.Warning);
            return;
        }

        var sessionDirectory = Path.Combine(
            Path.GetTempPath(),
            "StorageHub",
            "EditSessions",
            Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(sessionDirectory);
        var localPath = Path.Combine(sessionDirectory, fileName);
        await using (var stream = new FileStream(
            localPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(response.Content, cancellationToken).ConfigureAwait(true);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(true);
        }

        var session = new ExternalEditSession(
            owner,
            _client,
            sessionDirectory,
            localPath,
            response.Address,
            response.ContentType,
            maximumBytes,
            response.Content,
            () => FileUploaded?.Invoke(this, EventArgs.Empty),
            RemoveSession);
        _sessions.Add(session);
        try
        {
            session.Start(preferences.ExternalEditorPath);
        }
        catch
        {
            _sessions.Remove(session);
            session.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var session in _sessions.ToArray())
        {
            session.Dispose();
        }

        _sessions.Clear();
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    private void RemoveSession(ExternalEditSession session) => _sessions.Remove(session);

    private static string FormatLimit(int bytes) => $"{bytes / 1024:N0} KiB";

    private static void CreatePrivateDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The external-editor temporary directory cannot be a reparse point.");
        }

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user identity is unavailable.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static void ScavengeStaleSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageHub", "EditSessions");
        try
        {
            if (!Directory.Exists(root) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            foreach (var path in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var directory = new DirectoryInfo(path);
                    if ((directory.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        directory.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    {
                        directory.Delete(recursive: true);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Show(IWin32Window owner, string message, MessageBoxIcon icon) =>
        _ = MessageBox.Show(owner, message, "External editor", MessageBoxButtons.OK, icon);

    private sealed class ExternalEditSession : IDisposable
    {
        private readonly IWin32Window _owner;
        private readonly IObjectInspectorAgentClient _client;
        private readonly string _sessionDirectory;
        private readonly string _localPath;
        private readonly string? _contentType;
        private readonly int _maximumBytes;
        private readonly Action _uploaded;
        private readonly Action<ExternalEditSession> _closed;
        private readonly FileSystemWatcher _watcher;
        private readonly System.Windows.Forms.Timer _debounce;
        private ObjectInspectorAddress _address;
        private byte[] _observedHash;
        private bool _checking;
        private bool _disposed;

        internal ExternalEditSession(
            IWin32Window owner,
            IObjectInspectorAgentClient client,
            string sessionDirectory,
            string localPath,
            ObjectInspectorAddress address,
            string? contentType,
            int maximumBytes,
            byte[] originalContent,
            Action uploaded,
            Action<ExternalEditSession> closed)
        {
            _owner = owner;
            _client = client;
            _sessionDirectory = sessionDirectory;
            _localPath = localPath;
            _address = address;
            _contentType = contentType;
            _maximumBytes = maximumBytes;
            _uploaded = uploaded;
            _closed = closed;
            _observedHash = SHA256.HashData(originalContent);
            _debounce = new System.Windows.Forms.Timer { Interval = 900 };
            _debounce.Tick += DebounceTick;
            _watcher = new FileSystemWatcher(sessionDirectory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = false,
                SynchronizingObject = owner as ISynchronizeInvoke
            };
            _watcher.Changed += FileChanged;
            _watcher.Created += FileChanged;
            _watcher.Renamed += FileRenamed;
            _watcher.Deleted += FileChanged;
        }

        internal void Start(string? editorPath)
        {
            _watcher.EnableRaisingEvents = true;
            var start = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(editorPath) ? _localPath : editorPath,
                UseShellExecute = string.IsNullOrWhiteSpace(editorPath),
                WorkingDirectory = _sessionDirectory
            };
            if (!string.IsNullOrWhiteSpace(editorPath))
            {
                start.ArgumentList.Add(_localPath);
            }

            _ = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the configured editor.");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounce.Stop();
            _debounce.Tick -= DebounceTick;
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= FileChanged;
            _watcher.Created -= FileChanged;
            _watcher.Renamed -= FileRenamed;
            _watcher.Deleted -= FileChanged;
            _watcher.Dispose();
            _debounce.Dispose();
            try
            {
                Directory.Delete(_sessionDirectory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An editor may still hold the file. The uniquely named temp directory contains no credentials.
            }

            _closed(this);
        }

        private void FileChanged(object sender, FileSystemEventArgs e)
        {
            if (IsTarget(e.FullPath))
            {
                RestartDebounce();
            }
        }

        private void FileRenamed(object sender, RenamedEventArgs e)
        {
            if (IsTarget(e.FullPath) || IsTarget(e.OldFullPath))
            {
                RestartDebounce();
            }
        }

        private bool IsTarget(string path) =>
            string.Equals(Path.GetFullPath(path), _localPath, StringComparison.OrdinalIgnoreCase);

        private void RestartDebounce()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private async void DebounceTick(object? sender, EventArgs e)
        {
            _debounce.Stop();
            if (_checking || _disposed || !File.Exists(_localPath))
            {
                return;
            }

            _checking = true;
            try
            {
                var file = new FileInfo(_localPath);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    StopForUnsafeFile("The editor replaced the temporary file with a link. StorageHub will not upload it.");
                    return;
                }

                if (file.Length > _maximumBytes)
                {
                    _ = MessageBox.Show(
                        _owner,
                        $"The edited file is larger than the configured {_maximumBytes / 1024:N0} KiB limit and cannot be uploaded.",
                        "External editor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                byte[] content;
                await using (var stream = new FileStream(
                    _localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    content = new byte[file.Length];
                    await stream.ReadExactlyAsync(content).ConfigureAwait(true);
                }

                var hash = SHA256.HashData(content);
                if (CryptographicOperations.FixedTimeEquals(hash, _observedHash))
                {
                    return;
                }

                var choice = MessageBox.Show(
                    _owner,
                    $"'{Path.GetFileName(_localPath)}' changed in the external editor. Upload it back to the remote connection?",
                    "Upload edited file",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (choice == DialogResult.Cancel)
                {
                    Dispose();
                    return;
                }

                if (choice != DialogResult.Yes)
                {
                    _observedHash = hash;
                    return;
                }

                var response = await _client.UploadEditedFileAsync(new EditableFileUploadRequest(
                    EditableFileIpcContract.CurrentVersion,
                    _address,
                    content,
                    _contentType)).ConfigureAwait(true);
                if (response.Failure is not null)
                {
                    _ = MessageBox.Show(
                        _owner,
                        response.Failure.Message,
                        "Upload edited file",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                _address = response.Address;
                _observedHash = hash;
                _uploaded();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or TimeoutException)
            {
                _ = MessageBox.Show(
                    _owner,
                    $"StorageHub could not process the edited file. {error.Message}",
                    "External editor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                _checking = false;
            }
        }

        private void StopForUnsafeFile(string message)
        {
            _ = MessageBox.Show(
                _owner,
                message,
                "External editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            Dispose();
        }
    }
}
