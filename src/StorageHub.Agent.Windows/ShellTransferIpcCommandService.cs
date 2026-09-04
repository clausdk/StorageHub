using System.Collections.Concurrent;
using StorageHub.Agent.Ipc;
using StorageHub.Agent.Transfers;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Models;
using StorageHub.Transfers;

namespace StorageHub.Agent.Windows;

/// <summary>Creates short-lived immutable Explorer drop reviews and turns an approved review into
/// ordinary durable copy jobs. The agent, rather than the desktop process, captures the source
/// evidence so a restart/retry cannot silently follow a changed or linked path.</summary>
public sealed class ShellTransferIpcCommandService(
    ITransferJobStore store,
    ITransferEndpointConnector connector,
    TimeProvider? timeProvider = null) : IAgentIpcCommandHandler
{
    private static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(5);
    private readonly ITransferJobStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ITransferEndpointConnector _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Review> _reviews = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ExportJob> _exports = new();
    private readonly ConcurrentDictionary<string, PendingExplorerDrop> _pendingExplorerDrops = new(StringComparer.OrdinalIgnoreCase);

    public bool CanHandle(string messageType) => messageType is ShellTransferIpcMessageTypes.PlanImportRequest or
        ShellTransferIpcMessageTypes.CommitImportRequest or ShellTransferIpcMessageTypes.PrepareExportRequest or
        ShellTransferIpcMessageTypes.StartExportRequest or ShellTransferIpcMessageTypes.ExportStatusRequest or
        ShellTransferIpcMessageTypes.BeginExplorerDropRequest or ShellTransferIpcMessageTypes.CommitExplorerDropRequest;

    public async ValueTask<AgentIpcCommandResponse> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken = default) => request.MessageType switch
    {
        ShellTransferIpcMessageTypes.PlanImportRequest => await PlanAsync(request, cancellationToken).ConfigureAwait(false),
        ShellTransferIpcMessageTypes.CommitImportRequest => await CommitAsync(request, cancellationToken).ConfigureAwait(false),
        ShellTransferIpcMessageTypes.PrepareExportRequest => await PrepareExportAsync(request, cancellationToken).ConfigureAwait(false),
        ShellTransferIpcMessageTypes.StartExportRequest => StartExport(request),
        ShellTransferIpcMessageTypes.ExportStatusRequest => ExportStatus(request),
        ShellTransferIpcMessageTypes.BeginExplorerDropRequest => BeginExplorerDrop(request),
        ShellTransferIpcMessageTypes.CommitExplorerDropRequest => CommitExplorerDrop(request),
        _ => AgentIpcCommandResponse.Error("ipc.message.unsupported", "The requested shell transfer operation is not supported.")
    };

    private AgentIpcCommandResponse BeginExplorerDrop(IpcEnvelope envelope)
    {
        var request = envelope.DeserializePayload<ShellExportPrepareRequest>();
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
            return BeginExplorerDropFailure("The selected Explorer drop items are invalid.");

        PurgePendingExplorerDrops();
        if (_pendingExplorerDrops.Count >= 16)
            return BeginExplorerDropFailure("Too many Explorer drops are waiting for a destination.");

        var token = Guid.NewGuid().ToString("N");
        var markerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub", "DragMarkers");
        var inboxRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub", "ShellDropInbox");
        var markerPath = Path.Combine(markerRoot, "StorageHubDrop-" + token);
        try
        {
            Directory.CreateDirectory(markerRoot);
            Directory.CreateDirectory(inboxRoot);
            Directory.CreateDirectory(markerPath);
            File.WriteAllText(Path.Combine(markerPath, ".storagehub-drop"), token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(markerPath);
            return BeginExplorerDropFailure("StorageHub could not create the Explorer drop marker.");
        }

        _pendingExplorerDrops[token] = new PendingExplorerDrop(
            _time.GetUtcNow().AddMinutes(5), request.Sources, markerPath);
        return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.BeginExplorerDropResponse,
            new ExplorerDropBeginResponse(ShellTransferIpcContract.CurrentVersion, token, markerPath));
    }

    private AgentIpcCommandResponse CommitExplorerDrop(IpcEnvelope envelope)
    {
        var request = envelope.DeserializePayload<ExplorerDropCommitRequest>();
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds ||
            !_pendingExplorerDrops.TryRemove(request.DropToken, out var pending) ||
            pending.ExpiresAtUtc < _time.GetUtcNow())
            return CommitExplorerDropFailure("This Explorer drop has expired. Drag the selection again.");

        var receiptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub", "ShellDropInbox", request.DropToken + ".drop");
        string destination;
        try
        {
            destination = File.ReadAllText(receiptPath).Trim();
            File.Delete(receiptPath);
            TryDeleteDirectory(pending.MarkerPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(pending.MarkerPath);
            return CommitExplorerDropFailure("Explorer did not report a usable destination folder.");
        }

        if (string.IsNullOrWhiteSpace(destination) || destination.Length > ShellTransferIpcLimits.MaximumPathLength ||
            destination.Any(char.IsControl) || !Path.IsPathFullyQualified(destination) || !Directory.Exists(destination))
            return CommitExplorerDropFailure("The Explorer destination folder is unavailable or invalid.");

        var id = Guid.NewGuid();
        var job = new ExportJob(id, _time.GetUtcNow());
        if (!_exports.TryAdd(id, job)) return CommitExplorerDropFailure("The Explorer transfer could not be started.");
        _ = Task.Run(() => RunQueuedExportToDestinationAsync(job, pending.Sources, destination), CancellationToken.None);
        return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.CommitExplorerDropResponse,
            new ExplorerDropCommitResponse(ShellTransferIpcContract.CurrentVersion, true, id, destination));
    }

    private AgentIpcCommandResponse StartExport(IpcEnvelope envelope)
    {
        var request = envelope.DeserializePayload<ShellExportPrepareRequest>();
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
            return StartExportFailure("The selected export items are invalid.");

        PurgeFinishedExports();
        if (_exports.Count >= 16)
            return StartExportFailure("Too many Explorer exports are already active. Wait for an existing export to finish.");

        var id = Guid.NewGuid();
        var job = new ExportJob(id, _time.GetUtcNow());
        if (!_exports.TryAdd(id, job)) return StartExportFailure("The Explorer export could not be started.");
        _ = Task.Run(() => RunQueuedExportAsync(job, request), CancellationToken.None);
        return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.StartExportResponse,
            new ShellExportStartResponse(ShellTransferIpcContract.CurrentVersion, id));
    }

    private AgentIpcCommandResponse ExportStatus(IpcEnvelope envelope)
    {
        var request = envelope.DeserializePayload<ShellExportStatusRequest>();
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds ||
            !_exports.TryGetValue(request.ExportId, out var job))
        {
            return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.ExportStatusResponse,
                new ShellExportStatusResponse(ShellTransferIpcContract.CurrentVersion, request.ExportId,
                    ShellExportState.Failed, 0, 0, 0, [], Failure("The Explorer export job was not found.")));
        }
        return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.ExportStatusResponse, job.Snapshot());
    }

    private async Task RunQueuedExportAsync(ExportJob job, ShellExportPrepareRequest request)
    {
        var exportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub", "ShellExports");
        TryPurgeOldExports(exportRoot);
        var staging = Path.Combine(exportRoot, job.Id.ToString("N"));
        var transferIds = new List<TransferJobId>();
        try
        {
            Directory.CreateDirectory(staging);
            job.ChangeState(ShellExportState.Discovering, _time.GetUtcNow());
            var localPaths = new List<string>(request.Sources.Length);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in request.Sources)
            {
                if (!TryAddress(source.Address, out var address))
                    throw new ShellImportException("A selected remote address is invalid.");
                var opened = await _connector.OpenAsync(address, CancellationToken.None).ConfigureAwait(false);
                if (opened.IsFailure)
                    throw new ShellImportException("The selected connection could not be opened for Explorer.");
                await using var connection = opened.Value;
                if (!string.Equals(connection.Session.RootIdentity, address.RootIdentity, StringComparison.Ordinal))
                    throw new ShellImportException("The connection root changed before the Explorer export began.");

                var safeName = MakeUniqueFileName(MakeSafeFileName(source.DisplayName), usedNames);
                var localPath = Path.Combine(staging, safeName);
                localPaths.Add(localPath);
                if (source.IsDirectory)
                {
                    Directory.CreateDirectory(localPath);
                    await QueueDirectoryExportAsync(job, connection.Session, address, staging, localPath, transferIds)
                        .ConfigureAwait(false);
                }
                else
                {
                    var entry = await connection.Session.GetEntryAsync(address, CancellationToken.None).ConfigureAwait(false);
                    if (entry.IsFailure || entry.Value.Kind != StorageEntryKind.File)
                        throw new ShellImportException("A selected remote file could not be inspected.");
                    job.Discovered(_time.GetUtcNow());
                    await QueueExportFileAsync(entry.Value, staging, localPath, transferIds).ConfigureAwait(false);
                }
            }

            job.SetPaths([.. localPaths], _time.GetUtcNow());
            job.ChangeState(ShellExportState.Transferring, _time.GetUtcNow());
            await WaitForQueuedExportsAsync(job, transferIds).ConfigureAwait(false);
            job.Complete(_time.GetUtcNow());
        }
        catch (ShellImportException error)
        {
            // Once durable jobs exist, their workers own the staged paths. A later export purge
            // removes partial output without racing an in-flight write.
            if (transferIds.Count == 0) TryDeleteDirectory(staging);
            job.Fail(Failure(error.Message), _time.GetUtcNow());
        }
        catch (Exception)
        {
            if (transferIds.Count == 0) TryDeleteDirectory(staging);
            job.Fail(Failure("StorageHub could not prepare the selected items for Explorer."), _time.GetUtcNow());
        }
    }

    private async Task RunQueuedExportToDestinationAsync(
        ExportJob job,
        ShellExportSource[] sources,
        string destinationRoot)
    {
        var transferIds = new List<TransferJobId>();
        try
        {
            job.ChangeState(ShellExportState.Discovering, _time.GetUtcNow());
            var destinationPaths = new List<string>(sources.Length);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                if (!TryAddress(source.Address, out var address))
                    throw new ShellImportException("A selected remote address is invalid.");
                var opened = await _connector.OpenAsync(address, CancellationToken.None).ConfigureAwait(false);
                if (opened.IsFailure)
                    throw new ShellImportException("The selected connection could not be opened for Explorer.");
                await using var connection = opened.Value;
                if (!string.Equals(connection.Session.RootIdentity, address.RootIdentity, StringComparison.Ordinal))
                    throw new ShellImportException("The connection root changed before the Explorer transfer began.");

                var safeName = MakeUniqueFileName(MakeSafeFileName(source.DisplayName), usedNames);
                var destinationPath = Path.Combine(destinationRoot, safeName);
                destinationPaths.Add(destinationPath);
                if (source.IsDirectory)
                {
                    Directory.CreateDirectory(destinationPath);
                    await QueueDirectoryExportAsync(job, connection.Session, address, destinationRoot,
                        destinationPath, transferIds, directDestination: true).ConfigureAwait(false);
                }
                else
                {
                    var entry = await connection.Session.GetEntryAsync(address, CancellationToken.None).ConfigureAwait(false);
                    if (entry.IsFailure || entry.Value.Kind != StorageEntryKind.File)
                        throw new ShellImportException("A selected remote file could not be inspected.");
                    job.Discovered(_time.GetUtcNow());
                    await QueueExportFileAsync(entry.Value, destinationRoot, destinationPath, transferIds,
                        directDestination: true).ConfigureAwait(false);
                }
            }

            job.SetPaths([.. destinationPaths], _time.GetUtcNow());
            job.ChangeState(ShellExportState.Transferring, _time.GetUtcNow());
            await WaitForQueuedExportsAsync(job, transferIds).ConfigureAwait(false);
            job.Complete(_time.GetUtcNow());
        }
        catch (ShellImportException error)
        {
            job.Fail(Failure(error.Message), _time.GetUtcNow());
        }
        catch (Exception)
        {
            job.Fail(Failure("StorageHub could not queue the Explorer transfer."), _time.GetUtcNow());
        }
    }

    private async Task QueueDirectoryExportAsync(
        ExportJob job,
        StorageHub.Storage.Abstractions.IStorageEndpointSession session,
        StorageAddress root,
        string staging,
        string localRoot,
        List<TransferJobId> transferIds,
        bool directDestination = false)
    {
        var pending = new Queue<(StorageAddress Remote, string Local)>();
        pending.Enqueue((root, localRoot));
        while (pending.Count > 0)
        {
            var (remoteDirectory, localDirectory) = pending.Dequeue();
            string? continuation = null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            do
            {
                var page = await session.ListAsync(remoteDirectory,
                    new StorageListRequest(false, 1000, continuation), CancellationToken.None).ConfigureAwait(false);
                if (page.IsFailure) throw new ShellImportException("A selected remote folder could not be listed.");
                continuation = page.Value.ContinuationToken;
                foreach (var entry in page.Value.Entries)
                {
                    job.Discovered(_time.GetUtcNow());
                    if (job.DiscoveredEntries > ShellTransferIpcLimits.MaximumEntries)
                        throw new ShellImportException($"An Explorer export is limited to {ShellTransferIpcLimits.MaximumEntries:N0} files and folders.");
                    var local = Path.Combine(localDirectory,
                        MakeUniqueFileName(MakeSafeFileName(entry.Name), names));
                    if (entry.IsContainer)
                    {
                        Directory.CreateDirectory(local);
                        pending.Enqueue((entry.Address, local));
                    }
                    else if (entry.Kind == StorageEntryKind.File)
                    {
                        await QueueExportFileAsync(entry, staging, local, transferIds, directDestination).ConfigureAwait(false);
                    }
                }
            }
            while (continuation is not null);
        }
    }

    private async Task QueueExportFileAsync(
        StorageEntry source,
        string staging,
        string localPath,
        List<TransferJobId> transferIds,
        bool directDestination = false)
    {
        var relative = Path.GetRelativePath(staging, localPath).Replace(Path.DirectorySeparatorChar, '/');
        var destination = directDestination
            ? LocalStagingTransferEndpoint.CreateDestinationAddress(staging, relative)
            : LocalStagingTransferEndpoint.CreateAddress(staging, relative);
        if (destination.IsFailure) throw new ShellImportException("A local Explorer staging path is invalid.");
        var id = TransferJobId.New();
        var intent = new TransferIntent(id, TransferOperationKind.Copy, source.Address, destination.Value,
            source.Size, TransferVerificationPolicy.Size, _time.GetUtcNow());
        if (!await _store.TryEnqueueAsync(intent, priority: 0, CancellationToken.None).ConfigureAwait(false))
            throw new ShellImportException("A discovered file could not be added to the transfer queue.");
        transferIds.Add(id);
    }

    private async Task WaitForQueuedExportsAsync(ExportJob export, List<TransferJobId> transferIds)
    {
        if (transferIds.Count == 0) return;
        while (true)
        {
            var completed = 0;
            long completedBytes = 0;
            foreach (var id in transferIds)
            {
                var transfer = await _store.FindAsync(id, CancellationToken.None).ConfigureAwait(false)
                    ?? throw new ShellImportException("A queued Explorer transfer could not be found.");
                if (transfer.State.State == TransferState.Completed)
                {
                    completed++;
                    completedBytes += transfer.Intent.ExpectedLength ?? 0;
                }
                else if (transfer.State.State is TransferState.Failed or TransferState.Cancelled or
                         TransferState.Interrupted or TransferState.NeedsReconciliation or TransferState.RestartRequired)
                {
                    throw new ShellImportException("One or more queued Explorer transfers did not complete.");
                }
            }
            export.Progress(completed, completedBytes, _time.GetUtcNow());
            if (completed == transferIds.Count) return;
            await Task.Delay(200).ConfigureAwait(false);
        }
    }

    private void PurgeFinishedExports()
    {
        var cutoff = _time.GetUtcNow().AddMinutes(-15);
        foreach (var pair in _exports)
            if (pair.Value.IsFinishedBefore(cutoff)) _exports.TryRemove(pair.Key, out _);
    }

    private void PurgePendingExplorerDrops()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _pendingExplorerDrops)
        {
            if (pair.Value.ExpiresAtUtc >= now || !_pendingExplorerDrops.TryRemove(pair.Key, out var expired)) continue;
            TryDeleteDirectory(expired.MarkerPath);
            try
            {
                var receipt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StorageHub", "ShellDropInbox", pair.Key + ".drop");
                if (File.Exists(receipt)) File.Delete(receipt);
            }
            catch { }
        }
    }

    private async ValueTask<AgentIpcCommandResponse> PrepareExportAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ShellExportPrepareRequest>();
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds)
        {
            return ExportFailure("The selected export items are invalid.");
        }

        var exportRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageHub", "ShellExports");
        TryPurgeOldExports(exportRoot);
        var staging = Path.Combine(exportRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var localPaths = new List<string>(request.Sources.Length);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in request.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryAddress(source.Address, out var address))
                {
                    throw new ShellImportException("A selected remote address is invalid.");
                }

                var opened = await _connector.OpenAsync(address, cancellationToken).ConfigureAwait(false);
                if (opened.IsFailure)
                {
                    throw new ShellImportException("The selected connection could not be opened for Explorer.");
                }
                await using var connection = opened.Value;
                if (!string.Equals(connection.Session.RootIdentity, address.RootIdentity, StringComparison.Ordinal))
                {
                    throw new ShellImportException("The connection root changed before the Explorer export began.");
                }

                var safeName = MakeUniqueFileName(MakeSafeFileName(source.DisplayName), usedNames);
                var localPath = Path.Combine(staging, safeName);
                if (source.IsDirectory)
                {
                    Directory.CreateDirectory(localPath);
                    await ExportDirectoryAsync(connection.Session, address, localPath, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ExportFileAsync(connection.Session, address, localPath, cancellationToken).ConfigureAwait(false);
                }
                localPaths.Add(localPath);
            }

            return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.PrepareExportResponse,
                new ShellExportPrepareResponse(ShellTransferIpcContract.CurrentVersion, [.. localPaths]));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(staging);
            throw;
        }
        catch (ShellImportException error)
        {
            TryDeleteDirectory(staging);
            return ExportFailure(error.Message);
        }
        catch (Exception)
        {
            TryDeleteDirectory(staging);
            return ExportFailure("StorageHub could not prepare the selected items for Explorer.");
        }
    }

    private static async Task ExportDirectoryAsync(
        StorageHub.Storage.Abstractions.IStorageEndpointSession session,
        StorageAddress root,
        string localRoot,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<(StorageAddress Remote, string Local)>();
        pending.Enqueue((root, localRoot));
        var entries = 0;
        while (pending.Count > 0)
        {
            var (remoteDirectory, localDirectory) = pending.Dequeue();
            string? continuation = null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            do
            {
                var page = await session.ListAsync(remoteDirectory,
                    new StorageListRequest(false, 1000, continuation), cancellationToken).ConfigureAwait(false);
                if (page.IsFailure)
                {
                    throw new ShellImportException("A selected remote folder could not be listed.");
                }
                continuation = page.Value.ContinuationToken;
                foreach (var entry in page.Value.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++entries > ShellTransferIpcLimits.MaximumEntries)
                    {
                        throw new ShellImportException($"An Explorer export is limited to {ShellTransferIpcLimits.MaximumEntries:N0} files and folders.");
                    }
                    var name = MakeUniqueFileName(MakeSafeFileName(entry.Name), names);
                    var local = Path.Combine(localDirectory, name);
                    if (entry.IsContainer)
                    {
                        Directory.CreateDirectory(local);
                        pending.Enqueue((entry.Address, local));
                    }
                    else if (entry.Kind == StorageEntryKind.File)
                    {
                        await ExportFileAsync(session, entry.Address, local, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            while (continuation is not null);
        }
    }

    private static async Task ExportFileAsync(
        StorageHub.Storage.Abstractions.IStorageEndpointSession session,
        StorageAddress address,
        string localPath,
        CancellationToken cancellationToken)
    {
        var read = await session.OpenReadAsync(new StorageReadRequest(
            address,
            ExpectedVersionId: address.VersionId,
            ExpectedEntityTag: address.EntityTag), cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            throw new ShellImportException("A selected remote file could not be read.");
        }
        await using var source = read.Value;
        await using var destination = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) || character < ' ' ? '_' : character).ToArray()).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(safe)) safe = "item";
        var stem = Path.GetFileNameWithoutExtension(safe);
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "_" + safe;
        return safe.Length <= 240 ? safe : safe[..240];
    }

    private static string MakeUniqueFileName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem} ({number}){extension}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static void TryPurgeOldExports(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddHours(-12)) TryDeleteDirectory(directory);
            }
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private async ValueTask<AgentIpcCommandResponse> PlanAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ShellImportPlanRequest>();
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds || !TryAddress(request.Destination, out var destination)) return PlanFailure("The dropped paths or destination are invalid.");
        PurgeExpired();
        var files = new List<PlannedFile>();
        var directories = new List<PlannedDirectory>();
        try
        {
            foreach (var source in request.SourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Expand(source, files, directories, cancellationToken);
            }
        }
        catch (ShellImportException error) { return PlanFailure(error.Message); }
        catch (Exception) { return PlanFailure("StorageHub could not safely inspect the dropped filesystem items."); }
        if (files.Count + directories.Count == 0) return PlanFailure("The drop did not contain any normal files or folders.");
        if (files.Count + directories.Count > ShellTransferIpcLimits.MaximumEntries)
        {
            return PlanFailure($"A shell import is limited to {ShellTransferIpcLimits.MaximumEntries:N0} files and folders.");
        }
        if (files.Select(file => file.RelativePath).Concat(directories.Select(directory => directory.RelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count + directories.Count)
        {
            return PlanFailure("The dropped items contain more than one source for the same destination path.");
        }

        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingDestinations = new Dictionary<string, StorageAddress>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var opened = await _connector.OpenAsync(destination, cancellationToken).ConfigureAwait(false);
            if (opened.IsSuccess)
            {
                await using var connection = opened.Value;
                foreach (var file in files)
                {
                    var target = destination.Parent.Append(destination.Name.Length == 0 ? file.RelativePath : destination.CanonicalRelativePath + "/" + file.RelativePath);
                    if (target.IsSuccess)
                    {
                        var existing = await connection.Session.GetEntryAsync(target.Value, cancellationToken).ConfigureAwait(false);
                        if (existing.IsSuccess) { conflicts.Add(file.RelativePath); existingDestinations[file.RelativePath] = existing.Value.Address; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { /* reviewing an unavailable endpoint is still safe; commit will re-open it */ }

        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        _reviews[token] = new Review(
            _time.GetUtcNow().Add(ReviewLifetime),
            destination,
            files,
            directories,
            conflicts,
            existingDestinations);
        return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.PlanImportResponse,
            new ShellImportPlanResponse(ShellTransferIpcContract.CurrentVersion, token,
                directories.Select(directory => new ShellImportItem(directory.RelativePath, true, null, false))
                    .Concat(files.Select(file => new ShellImportItem(
                        file.RelativePath, false, file.File.Length, conflicts.Contains(file.RelativePath))))
                    .ToArray()));
    }

    private async ValueTask<AgentIpcCommandResponse> CommitAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.DeserializePayload<ShellImportCommitRequest>();
        if (!ShellTransferIpcContract.IsSupported(request.ContractVersion) || !request.HasValidBounds) return CommitFailure("The import review request is invalid.");
        if (!_reviews.TryRemove(request.ReviewToken, out var review) || review.ExpiresAtUtc < _time.GetUtcNow()) return CommitFailure("This import review has expired. Drop the items again to review current files.");
        if (request.Disposition == ShellImportDisposition.Cancel) return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.CommitImportResponse, new ShellImportCommitResponse(ShellTransferIpcContract.CurrentVersion, false, [], null));
        if (review.Directories.Count > 0)
        {
            var opened = await _connector.OpenAsync(review.Destination, cancellationToken).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                return CommitFailure("The destination could not be opened to create the reviewed folders.");
            }
            await using var connection = opened.Value;
            foreach (var directory in review.Directories
                .OrderBy(static item => item.RelativePath.Count(static character => character == '/'))
                .ThenBy(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var target = review.Destination.Append(directory.RelativePath);
                if (target.IsFailure)
                {
                    return CommitFailure("A reviewed destination folder path is no longer valid.");
                }

                var existing = await connection.Session.GetEntryAsync(target.Value, cancellationToken).ConfigureAwait(false);
                if (existing.IsSuccess)
                {
                    if (!existing.Value.IsContainer)
                    {
                        return CommitFailure("A file conflicts with one of the reviewed destination folders.");
                    }
                    continue;
                }
                if (existing.Error.Kind != StorageFailureKind.NotFound)
                {
                    return CommitFailure("A reviewed destination folder could not be inspected safely.");
                }

                var created = await connection.Session.CreateDirectoryAsync(target.Value, cancellationToken).ConfigureAwait(false);
                if (created.IsFailure)
                {
                    return CommitFailure("A reviewed destination folder could not be created.");
                }
            }
        }
        var ids = new List<Guid>();
        foreach (var file in review.Files)
        {
            if (request.Disposition == ShellImportDisposition.SkipConflictingFiles && review.Conflicts.Contains(file.RelativePath)) continue;
            var source = LocalFilesystemTransferEndpoint.CreateAddress(file.Root, file.RelativePath, file.File);
            if (source.IsFailure) return CommitFailure("A dropped source file is no longer valid. No remaining files were queued.");
            var target = review.Destination.Append(file.RelativePath);
            if (target.IsFailure) return CommitFailure("A destination path from the reviewed import is no longer valid.");
            var destination = target.Value;
            if (review.ExistingDestinations.TryGetValue(file.RelativePath, out var existing))
            {
                var withEvidence = StorageAddress.Create(destination.ProfileId, destination.RootIdentity, destination.CanonicalRelativePath, existing.NativeItemId, existing.VersionId, existing.EntityTag);
                if (withEvidence.IsFailure) return CommitFailure("The reviewed destination identity is invalid.");
                destination = withEvidence.Value;
            }
            var intent = new TransferIntent(TransferJobId.New(), TransferOperationKind.Copy, source.Value, destination, file.File.Length, TransferVerificationPolicy.Size, _time.GetUtcNow());
            var accepted = await _store.TryEnqueueAsync(intent, priority: 0, cancellationToken).ConfigureAwait(false);
            if (accepted) ids.Add(intent.TransferJobId.Value);
        }
        return AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.CommitImportResponse, new ShellImportCommitResponse(ShellTransferIpcContract.CurrentVersion, true, [.. ids]));
    }

    private static void Expand(
        string source,
        List<PlannedFile> files,
        List<PlannedDirectory> directories,
        CancellationToken token)
    {
        var full = Path.GetFullPath(source);
        var info = new FileInfo(full);
        if (info.Exists)
        {
            RejectReparse(info); AddFile(Path.GetDirectoryName(full)!, info, files); return;
        }
        var directory = new DirectoryInfo(full);
        if (!directory.Exists) throw new ShellImportException("A dropped source path no longer exists.");
        RejectReparse(directory);
        var root = directory.Parent?.FullName ?? throw new ShellImportException("A filesystem root cannot be imported directly.");
        foreach (var item in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Prepend(directory))
        {
            token.ThrowIfCancellationRequested(); RejectReparse(item);
            if (item is FileInfo file)
            {
                AddFile(root, file, files);
            }
            else if (item is DirectoryInfo child)
            {
                AddDirectory(root, child, directories);
            }
        }
    }

    private static void AddFile(string root, FileInfo file, List<PlannedFile> output)
    {
        if (output.Count >= ShellTransferIpcLimits.MaximumEntries) throw new ShellImportException($"A shell import is limited to {ShellTransferIpcLimits.MaximumEntries:N0} files.");
        var relative = Path.GetRelativePath(root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") throw new ShellImportException("A dropped source path escapes its approved root.");
        output.Add(new PlannedFile(root, relative, file));
    }

    private static void AddDirectory(string root, DirectoryInfo directory, List<PlannedDirectory> output)
    {
        var relative = Path.GetRelativePath(root, directory.FullName).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..")
        {
            throw new ShellImportException("A dropped folder escapes its approved root.");
        }
        output.Add(new PlannedDirectory(relative));
    }

    private static void RejectReparse(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new ShellImportException("Dropped links and reparse points are not supported.");
    }

    private static bool TryAddress(TransferQueueAddress wire, out StorageAddress address)
    {
        var result = StorageAddress.Create(new ConnectionProfileId(wire.ConnectionId), wire.RootIdentity, wire.RelativePath, wire.NativeItemId, wire.VersionId, wire.EntityTag);
        address = result.IsSuccess ? result.Value : null!; return result.IsSuccess;
    }
    private void PurgeExpired() { var now = _time.GetUtcNow(); foreach (var item in _reviews.Where(pair => pair.Value.ExpiresAtUtc < now)) _reviews.TryRemove(item.Key, out _); }
    private static AgentIpcCommandResponse PlanFailure(string message) => AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.PlanImportResponse, new ShellImportPlanResponse(ShellTransferIpcContract.CurrentVersion, null, [], Failure(message)));
    private static AgentIpcCommandResponse CommitFailure(string message) => AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.CommitImportResponse, new ShellImportCommitResponse(ShellTransferIpcContract.CurrentVersion, false, [], Failure(message)));
    private static AgentIpcCommandResponse ExportFailure(string message) => AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.PrepareExportResponse, new ShellExportPrepareResponse(ShellTransferIpcContract.CurrentVersion, [], Failure(message)));
    private static AgentIpcCommandResponse StartExportFailure(string message) => AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.StartExportResponse, new ShellExportStartResponse(ShellTransferIpcContract.CurrentVersion, Guid.Empty, Failure(message)));
    private static AgentIpcCommandResponse BeginExplorerDropFailure(string message) => AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.BeginExplorerDropResponse, new ExplorerDropBeginResponse(ShellTransferIpcContract.CurrentVersion, null, null, Failure(message)));
    private static AgentIpcCommandResponse CommitExplorerDropFailure(string message) => AgentIpcCommandResponse.Create(ShellTransferIpcMessageTypes.CommitExplorerDropResponse, new ExplorerDropCommitResponse(ShellTransferIpcContract.CurrentVersion, false, Guid.Empty, null, Failure(message)));
    private static StorageIpcFailure Failure(string message) => new("shell-transfer.invalid", StorageIpcFailureCategory.Validation, message, false);
    private sealed record PlannedFile(string Root, string RelativePath, FileInfo File);
    private sealed record PlannedDirectory(string RelativePath);
    private sealed record Review(
        DateTimeOffset ExpiresAtUtc,
        StorageAddress Destination,
        IReadOnlyList<PlannedFile> Files,
        IReadOnlyList<PlannedDirectory> Directories,
        IReadOnlySet<string> Conflicts,
        IReadOnlyDictionary<string, StorageAddress> ExistingDestinations);
    private sealed record PendingExplorerDrop(
        DateTimeOffset ExpiresAtUtc,
        ShellExportSource[] Sources,
        string MarkerPath);
    private sealed class ExportJob(Guid id, DateTimeOffset createdAt)
    {
        private readonly object _gate = new();
        private ShellExportState _state = ShellExportState.Queued;
        private int _discoveredEntries;
        private int _completedFiles;
        private long _completedBytes;
        private string[] _localPaths = [];
        private StorageIpcFailure? _failure;
        private DateTimeOffset _updatedAt = createdAt;

        public Guid Id { get; } = id;
        public int DiscoveredEntries { get { lock (_gate) return _discoveredEntries; } }

        public void ChangeState(ShellExportState state, DateTimeOffset now)
        { lock (_gate) { _state = state; _updatedAt = now; } }
        public void Discovered(DateTimeOffset now)
        { lock (_gate) { _discoveredEntries++; _updatedAt = now; } }
        public void SetPaths(string[] paths, DateTimeOffset now)
        { lock (_gate) { _localPaths = paths; _updatedAt = now; } }
        public void Progress(int completedFiles, long completedBytes, DateTimeOffset now)
        { lock (_gate) { _completedFiles = completedFiles; _completedBytes = completedBytes; _updatedAt = now; } }
        public void Complete(DateTimeOffset now)
        { lock (_gate) { _state = ShellExportState.Completed; _updatedAt = now; } }
        public void Fail(StorageIpcFailure failure, DateTimeOffset now)
        { lock (_gate) { _state = ShellExportState.Failed; _failure = failure; _localPaths = []; _updatedAt = now; } }
        public bool IsFinishedBefore(DateTimeOffset cutoff)
        { lock (_gate) return _state is (ShellExportState.Completed or ShellExportState.Failed) && _updatedAt < cutoff; }
        public ShellExportStatusResponse Snapshot()
        {
            lock (_gate)
            {
                return new ShellExportStatusResponse(ShellTransferIpcContract.CurrentVersion, Id, _state,
                    _discoveredEntries, _completedFiles, _completedBytes, [.. _localPaths], _failure);
            }
        }
    }
    private sealed class ShellImportException(string message) : Exception(message);
}
