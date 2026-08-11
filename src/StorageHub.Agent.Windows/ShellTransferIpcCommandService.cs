using System.Collections.Concurrent;
using StorageHub.Agent.Ipc;
using StorageHub.Agent.Transfers;
using StorageHub.Contracts.Ipc;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Identifiers;
using StorageHub.Domain.Storage;
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

    public bool CanHandle(string messageType) => messageType is ShellTransferIpcMessageTypes.PlanImportRequest or ShellTransferIpcMessageTypes.CommitImportRequest;

    public async ValueTask<AgentIpcCommandResponse> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken = default) => request.MessageType switch
    {
        ShellTransferIpcMessageTypes.PlanImportRequest => await PlanAsync(request, cancellationToken).ConfigureAwait(false),
        ShellTransferIpcMessageTypes.CommitImportRequest => await CommitAsync(request, cancellationToken).ConfigureAwait(false),
        _ => AgentIpcCommandResponse.Error("ipc.message.unsupported", "The requested shell transfer operation is not supported.")
    };

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
    private sealed class ShellImportException(string message) : Exception(message);
}
