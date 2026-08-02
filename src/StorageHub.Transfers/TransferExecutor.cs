using System.Security.Cryptography;
using StorageHub.Contracts.Results;
using StorageHub.Domain.Capabilities;
using StorageHub.Domain.Storage;
using StorageHub.Storage.Abstractions;
using StorageHub.Storage.Models;

namespace StorageHub.Transfers;

public sealed record TransferExecutionOptions(
    bool Overwrite = false,
    int BufferSize = BoundedStreamCopier.DefaultBufferSize);

public sealed record TransferExecutionReport(
    StorageEntry Destination,
    long BytesTransferred,
    bool SourceDeleted);

/// <summary>
/// Executes an any-to-any transfer without loading the item into memory. Provider sessions own
/// protocol details; this service owns copy, verification, and move-after-verification semantics.
/// </summary>
public static class TransferExecutor
{
    public static async ValueTask<StorageResult<TransferExecutionReport>> ExecuteAsync(
        TransferIntent intent,
        IStorageEndpointSession sourceSession,
        IStorageEndpointSession destinationSession,
        TransferExecutionOptions? options = null,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(sourceSession);
        ArgumentNullException.ThrowIfNull(destinationSession);
        options ??= new TransferExecutionOptions();
        if (options.BufferSize is <= 0 or > BoundedStreamCopier.MaximumBufferSize)
        {
            return Fail(
                "transfer.buffer.invalid",
                StorageFailureKind.Validation,
                $"The transfer buffer must be between 1 and {BoundedStreamCopier.MaximumBufferSize} bytes.");
        }

        var sourceAddressValidation = sourceSession.ValidateAddress(intent.Source);
        if (sourceAddressValidation.IsFailure)
        {
            return StorageResult<TransferExecutionReport>.Fail(sourceAddressValidation.Error);
        }

        var destinationAddressValidation = destinationSession.ValidateAddress(intent.Destination);
        if (destinationAddressValidation.IsFailure)
        {
            return StorageResult<TransferExecutionReport>.Fail(destinationAddressValidation.Error);
        }

        var capabilityValidation = ValidateCapabilities(intent, sourceSession, destinationSession, options);
        if (capabilityValidation.IsFailure)
        {
            return StorageResult<TransferExecutionReport>.Fail(capabilityValidation.Error);
        }

        var directConditionalOverwrite = UsesDirectConditionalOverwrite(
            destinationSession,
            options);

        if (options.Overwrite)
        {
            var destinationInfo = await destinationSession
                .GetEntryAsync(intent.Destination, cancellationToken)
                .ConfigureAwait(false);
            if (destinationInfo.IsFailure)
            {
                return StorageResult<TransferExecutionReport>.Fail(destinationInfo.Error);
            }

            if (!IdentityMatches(
                    destinationInfo.Value,
                    intent.ExpectedDestinationVersionId,
                    intent.ExpectedDestinationEntityTag))
            {
                return Fail(
                    "transfer.destination.changed",
                    StorageFailureKind.Conflict,
                    "The destination identity changed after the overwrite was approved.");
            }

            if (intent.ExpectedDestinationDigest is not null)
            {
                var destinationDigestFailure = await VerifyExpectedDestinationDigestAsync(
                    intent.ExpectedDestinationDigest,
                    destinationSession,
                    destinationInfo.Value,
                    cancellationToken).ConfigureAwait(false);
                if (destinationDigestFailure is not null)
                {
                    return StorageResult<TransferExecutionReport>.Fail(destinationDigestFailure);
                }
            }
        }

        var sourceInfo = await sourceSession
            .GetEntryAsync(intent.Source, cancellationToken)
            .ConfigureAwait(false);
        if (sourceInfo.IsFailure)
        {
            return StorageResult<TransferExecutionReport>.Fail(sourceInfo.Error);
        }

        if (sourceInfo.Value.Kind != StorageEntryKind.File)
        {
            return Fail(
                "transfer.source.not_file",
                StorageFailureKind.Unsupported,
                "Directory transfers must first be expanded into an operation plan.");
        }

        if (!IdentityMatches(
                sourceInfo.Value,
                intent.Source.VersionId,
                intent.Source.EntityTag))
        {
            return Fail(
                "transfer.source.changed",
                StorageFailureKind.Conflict,
                "The source identity changed after the transfer was queued.");
        }

        var expectedLength = intent.ExpectedLength ?? sourceInfo.Value.Size;
        if (intent.ExpectedLength is { } declaredLength &&
            sourceInfo.Value.Size is { } observedLength &&
            declaredLength != observedLength)
        {
            return Fail(
                "transfer.source.changed",
                StorageFailureKind.Conflict,
                "The source length changed after the transfer was queued.");
        }

        var read = await sourceSession.OpenReadAsync(
            new StorageReadRequest(
                intent.Source,
                ExpectedVersionId: intent.Source.VersionId,
                // ETag-only sources are checked immediately before and after streaming. This
                // avoids pretending every adapter can apply an ETag download condition while
                // still using the ETag strictly as a side-local mutation identity, never a hash.
                ExpectedEntityTag: null),
            cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return StorageResult<TransferExecutionReport>.Fail(read.Error);
        }

        await using var source = read.Value;
        var writeAddressResult = options.Overwrite && !directConditionalOverwrite
            ? CreateStagingAddress(intent)
            : StorageResult<StorageAddress>.Success(intent.Destination);
        if (writeAddressResult.IsFailure)
        {
            return StorageResult<TransferExecutionReport>.Fail(writeAddressResult.Error);
        }

        var writeAddress = writeAddressResult.Value;
        var write = await destinationSession.OpenWriteAsync(
            new StorageWriteRequest(
                writeAddress,
                directConditionalOverwrite
                    ? StorageWriteMode.Overwrite
                    : StorageWriteMode.CreateNew,
                expectedLength,
                expectedDestinationVersionId: directConditionalOverwrite
                    ? intent.ExpectedDestinationVersionId
                    : null,
                expectedDestinationEntityTag: directConditionalOverwrite
                    ? intent.ExpectedDestinationEntityTag
                    : null),
            cancellationToken).ConfigureAwait(false);
        if (write.IsFailure)
        {
            return StorageResult<TransferExecutionReport>.Fail(write.Error);
        }

        await using var destination = write.Value;
        StreamCopyResult copied;
        var computeCopyDigest = intent.ExpectedSourceDigest is not null ||
                                intent.RequiredDestinationDigest is not null ||
                                intent.VerificationPolicy != TransferVerificationPolicy.Size;
        try
        {
            copied = computeCopyDigest
                ? await BoundedStreamCopier.CopyWithSha256Async(
                    source,
                    destination.Content,
                    expectedLength,
                    options.BufferSize,
                    progress,
                    cancellationToken).ConfigureAwait(false)
                : await BoundedStreamCopier.CopyAsync(
                    source,
                    destination.Content,
                    expectedLength,
                    options.BufferSize,
                    progress,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await BestEffortAbortAsync(destination).ConfigureAwait(false);
            throw;
        }
        catch (EndOfStreamException)
        {
            await BestEffortAbortAsync(destination).ConfigureAwait(false);
            return Fail(
                "transfer.source.truncated",
                StorageFailureKind.Integrity,
                "The source ended before the expected number of bytes was read.");
        }
        catch (SourceLengthExceededException)
        {
            await BestEffortAbortAsync(destination).ConfigureAwait(false);
            return Fail(
                "transfer.source.grew",
                StorageFailureKind.Integrity,
                "The source grew after its expected length was captured; the destination was not committed.");
        }
        catch (IOException)
        {
            await BestEffortAbortAsync(destination).ConfigureAwait(false);
            return Fail(
                "transfer.stream.failed",
                StorageFailureKind.Provider,
                "The provider stream failed while transferring the item.",
                isTransient: true);
        }

        if (intent.Source.VersionId is null && intent.Source.EntityTag is not null)
        {
            var sourceAfterCopy = await sourceSession
                .GetEntryAsync(intent.Source, cancellationToken)
                .ConfigureAwait(false);
            if (sourceAfterCopy.IsFailure ||
                !IdentityMatches(
                    sourceAfterCopy.Value,
                    expectedVersionId: null,
                    expectedEntityTag: intent.Source.EntityTag) ||
                sourceAfterCopy.Value.Size != sourceInfo.Value.Size)
            {
                await BestEffortAbortAsync(destination).ConfigureAwait(false);
                return Fail(
                    "transfer.source.changed",
                    StorageFailureKind.Conflict,
                    "The source identity changed while the transfer was being streamed; the destination was not published.");
            }
        }

        var copiedDigestFailure = VerifyCopiedDigest(intent, copied);
        if (copiedDigestFailure is not null)
        {
            await BestEffortAbortAsync(destination).ConfigureAwait(false);
            return StorageResult<TransferExecutionReport>.Fail(copiedDigestFailure);
        }

        var commit = await destination.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (commit.IsFailure)
        {
            await BestEffortAbortAsync(destination).ConfigureAwait(false);
            return StorageResult<TransferExecutionReport>.Fail(commit.Error);
        }

        var verification = VerifyLengths(commit.Value, copied.BytesCopied, expectedLength);
        verification ??= await VerifyDestinationDigestAsync(
            intent,
            destinationSession,
            commit.Value,
            copied,
            cancellationToken).ConfigureAwait(false);
        if (verification is not null)
        {
            if (options.Overwrite && !directConditionalOverwrite)
            {
                await BestEffortDeleteStagingAsync(destinationSession, commit.Value).ConfigureAwait(false);
            }

            return StorageResult<TransferExecutionReport>.Fail(
                directConditionalOverwrite
                    ? new StorageFailure(
                        verification.Code,
                        verification.Kind,
                        $"{verification.Message} The atomic conditional replacement completed; reconciliation is required.",
                        verification.IsTransient,
                        verification.ProviderCode,
                        verification.DiagnosticId)
                    : verification);
        }

        var committedDestination = commit.Value;
        if (options.Overwrite && !directConditionalOverwrite)
        {
            if (!IsSameLogicalAddress(commit.Value.Address, writeAddress))
            {
                return Fail(
                    "transfer.staging.identity_mismatch",
                    StorageFailureKind.Integrity,
                    "The provider returned a different staging identity; the destination was not promoted.");
            }

            var promote = await destinationSession.MoveAsync(
                new StorageMoveRequest(
                    commit.Value.Address,
                    intent.Destination,
                    Overwrite: true,
                    ExpectedSourceVersionId: commit.Value.Address.VersionId,
                    ExpectedDestinationVersionId: intent.ExpectedDestinationVersionId),
                cancellationToken).ConfigureAwait(false);
            if (promote.IsFailure)
            {
                await BestEffortDeleteStagingAsync(destinationSession, commit.Value).ConfigureAwait(false);
                return StorageResult<TransferExecutionReport>.Fail(new StorageFailure(
                    "transfer.promote.failed",
                    promote.Error.Kind,
                    "The verified staging object could not be conditionally promoted; the original destination was preserved.",
                    promote.Error.IsTransient,
                    promote.Error.ProviderCode,
                    promote.Error.DiagnosticId));
            }

            verification = VerifyLengths(promote.Value, copied.BytesCopied, expectedLength);
            if (verification is not null)
            {
                return StorageResult<TransferExecutionReport>.Fail(new StorageFailure(
                    verification.Code,
                    verification.Kind,
                    $"{verification.Message} The atomic promote completed; reconciliation is required.",
                    verification.IsTransient,
                    verification.ProviderCode,
                    verification.DiagnosticId));
            }

            committedDestination = promote.Value;
        }

        var sourceDeleted = false;
        if (intent.Operation == TransferOperationKind.Move)
        {
            var deletion = await sourceSession.DeleteAsync(
                new StorageDeleteRequest(
                    intent.Source,
                    Recursive: false,
                    IgnoreMissing: false,
                    ExpectedVersionId: intent.Source.VersionId,
                    ExpectedEntityTag: intent.Source.EntityTag),
                cancellationToken).ConfigureAwait(false);
            if (deletion.IsFailure)
            {
                return StorageResult<TransferExecutionReport>.Fail(new StorageFailure(
                    "transfer.move.cleanup_failed",
                    StorageFailureKind.Conflict,
                    "The destination was verified, but the source could not be deleted. Reconciliation is required.",
                    deletion.Error.IsTransient,
                    deletion.Error.ProviderCode,
                    deletion.Error.DiagnosticId));
            }

            sourceDeleted = true;
        }

        return StorageResult<TransferExecutionReport>.Success(new TransferExecutionReport(
            committedDestination,
            copied.BytesCopied,
            sourceDeleted));
    }

    private static StorageResult ValidateCapabilities(
        TransferIntent intent,
        IStorageEndpointSession sourceSession,
        IStorageEndpointSession destinationSession,
        TransferExecutionOptions options)
    {
        var read = RequireSupported(sourceSession, StorageFeature.ReadStream, "read the transfer source");
        if (read.IsFailure)
        {
            return read;
        }

        var write = RequireSupported(destinationSession, StorageFeature.WriteStream, "write the transfer destination");
        if (write.IsFailure)
        {
            return write;
        }

        if (!options.Overwrite)
        {
            var conditionalCreate = RequireNative(
                destinationSession,
                StorageFeature.ConditionalCreate,
                "create the transfer destination without racing an existing object");
            if (conditionalCreate.IsFailure)
            {
                return conditionalCreate;
            }
        }

        if (!options.Overwrite && intent.ExpectedDestinationDigest is not null)
        {
            return UnsafeMutation(
                "transfer.destination.hash_precondition_invalid",
                "A planned destination digest is only valid for an approved overwrite.");
        }

        if ((intent.VerificationPolicy == TransferVerificationPolicy.StrongHashRequired ||
             intent.ExpectedDestinationDigest is not null ||
             intent.RequiredDestinationDigest is not null) &&
            destinationSession is not IStoragePortableChecksumSession)
        {
            return UnsafeMutation(
                "transfer.verification.hash_unavailable",
                "Required destination SHA-256 verification is unavailable for this endpoint.");
        }

        if (options.Overwrite)
        {
            if (string.IsNullOrWhiteSpace(intent.ExpectedDestinationVersionId) &&
                string.IsNullOrWhiteSpace(intent.ExpectedDestinationEntityTag))
            {
                return UnsafeMutation(
                    "transfer.overwrite.identity_required",
                    "An overwrite requires the exact destination version or entity tag captured during planning.");
            }

            if (UsesDirectConditionalOverwrite(destinationSession, options))
            {
                foreach (var feature in new[]
                         {
                             StorageFeature.ConditionalUpdate,
                             StorageFeature.AtomicReplace,
                         })
                {
                    var capability = RequireNative(
                        destinationSession,
                        feature,
                        "atomically replace the approved destination identity");
                    if (capability.IsFailure)
                    {
                        return capability;
                    }
                }
            }
            else
            {
                var conditionalCreate = RequireNative(
                    destinationSession,
                    StorageFeature.ConditionalCreate,
                    "create a unique staging object without a collision");
                if (conditionalCreate.IsFailure)
                {
                    return conditionalCreate;
                }

                foreach (var feature in new[]
                         {
                             StorageFeature.ObjectVersioning,
                             StorageFeature.TemporaryFiles,
                             StorageFeature.FileMove,
                             StorageFeature.AtomicRename,
                         })
                {
                    var capability = RequireNative(
                        destinationSession,
                        feature,
                        "stage, verify, and conditionally promote an overwrite");
                    if (capability.IsFailure)
                    {
                        return capability;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(intent.Source.VersionId) &&
                string.IsNullOrWhiteSpace(intent.Source.EntityTag) &&
                intent.ExpectedSourceDigest is null)
            {
                return UnsafeMutation(
                    "transfer.overwrite.source_evidence_required",
                    "An overwrite requires an exact source version, entity tag, or planned portable SHA-256 evidence.");
            }

            if (intent.Source.VersionId is not null)
            {
                var conditionalRead = RequireNative(
                    sourceSession,
                    StorageFeature.ObjectVersioning,
                    "conditionally read the source for an overwrite");
                if (conditionalRead.IsFailure)
                {
                    return conditionalRead;
                }
            }
        }

        if (intent.Operation == TransferOperationKind.Move)
        {
            if (string.IsNullOrWhiteSpace(intent.Source.VersionId))
            {
                return UnsafeMutation(
                    "transfer.move.version_required",
                    "A move requires the exact source version captured during planning before it can be deleted.");
            }

            var delete = RequireSupported(sourceSession, StorageFeature.Delete, "delete the verified move source");
            if (delete.IsFailure)
            {
                return delete;
            }

            var conditionalDelete = RequireNative(
                sourceSession,
                StorageFeature.ConditionalDelete,
                "conditionally delete the verified move source");
            if (conditionalDelete.IsFailure)
            {
                return conditionalDelete;
            }
        }

        return StorageResult.Success();
    }

    private static bool UsesDirectConditionalOverwrite(
        IStorageEndpointSession destinationSession,
        TransferExecutionOptions options) =>
        options.Overwrite &&
        destinationSession.Capabilities[StorageFeature.ConditionalUpdate].Level == FeatureSupportLevel.Native &&
        destinationSession.Capabilities[StorageFeature.AtomicReplace].Level == FeatureSupportLevel.Native;

    private static bool IdentityMatches(
        StorageEntry current,
        string? expectedVersionId,
        string? expectedEntityTag)
    {
        var currentEntityTag = current.Address.EntityTag ?? current.ETag;
        return (expectedVersionId is null || StringComparer.Ordinal.Equals(
                    current.Address.VersionId,
                    expectedVersionId)) &&
               (expectedEntityTag is null || StringComparer.Ordinal.Equals(
                    currentEntityTag,
                    expectedEntityTag));
    }

    private static StorageResult RequireSupported(
        IStorageEndpointSession session,
        StorageFeature feature,
        string purpose) => session.Capabilities.Supports(feature)
        ? StorageResult.Success()
        : UnsafeMutation(
            "transfer.capability.unsupported",
            $"The endpoint cannot {purpose} because it does not support {feature}.");

    private static StorageResult RequireNative(
        IStorageEndpointSession session,
        StorageFeature feature,
        string purpose) => session.Capabilities[feature].Level == FeatureSupportLevel.Native
        ? StorageResult.Success()
        : UnsafeMutation(
            "transfer.conditional_mutation.unsupported",
            $"The endpoint cannot safely {purpose} because native {feature} support is unavailable.");

    private static StorageResult UnsafeMutation(string code, string message) => StorageResult.Fail(
        new StorageFailure(code, StorageFailureKind.Unsupported, message));

    private static StorageResult<StorageAddress> CreateStagingAddress(TransferIntent intent) =>
        intent.Destination.Parent.Append($".storagehub-{intent.TransferJobId.Value:N}.staging");

    private static bool IsSameLogicalAddress(StorageAddress left, StorageAddress right) =>
        left.ProfileId == right.ProfileId &&
        StringComparer.Ordinal.Equals(left.RootIdentity, right.RootIdentity) &&
        StringComparer.Ordinal.Equals(left.CanonicalRelativePath, right.CanonicalRelativePath);

    private static StorageFailure? VerifyLengths(
        StorageEntry destination,
        long copiedBytes,
        long? expectedLength)
    {
        if (expectedLength is { } expected && copiedBytes != expected)
        {
            return new StorageFailure(
                "transfer.verification.length_mismatch",
                StorageFailureKind.Integrity,
                "The transferred byte count did not match the expected source length.");
        }

        if (destination.Size is { } destinationLength && destinationLength != copiedBytes)
        {
            return new StorageFailure(
                "transfer.verification.destination_length_mismatch",
                StorageFailureKind.Integrity,
                "The committed destination length did not match the transferred byte count.");
        }

        return null;
    }

    private static StorageFailure? VerifyCopiedDigest(
        TransferIntent intent,
        StreamCopyResult copied)
    {
        var digestRequired = intent.ExpectedSourceDigest is not null ||
                             intent.RequiredDestinationDigest is not null ||
                             intent.VerificationPolicy != TransferVerificationPolicy.Size;
        if (!digestRequired)
        {
            return null;
        }

        if (copied.PortableDigest is null)
        {
            return new StorageFailure(
                "transfer.verification.hash_unavailable",
                StorageFailureKind.Integrity,
                "The transfer copy did not produce portable SHA-256 evidence.");
        }

        if (intent.ExpectedSourceDigest is not null &&
            !DigestEquals(intent.ExpectedSourceDigest, copied.PortableDigest))
        {
            return new StorageFailure(
                "transfer.verification.source_hash_mismatch",
                StorageFailureKind.Integrity,
                "The source SHA-256 did not match the approved plan; the destination was not published.");
        }

        return intent.RequiredDestinationDigest is not null &&
               !DigestEquals(intent.RequiredDestinationDigest, copied.PortableDigest)
            ? new StorageFailure(
                "transfer.verification.planned_destination_hash_mismatch",
                StorageFailureKind.Integrity,
                "The copied bytes cannot satisfy the required destination SHA-256; the destination was not published.")
            : null;
    }

    private static async ValueTask<StorageFailure?> VerifyDestinationDigestAsync(
        TransferIntent intent,
        IStorageEndpointSession destinationSession,
        StorageEntry destination,
        StreamCopyResult copied,
        CancellationToken cancellationToken)
    {
        var mustVerify = intent.VerificationPolicy == TransferVerificationPolicy.StrongHashRequired ||
                         intent.RequiredDestinationDigest is not null;
        if (intent.VerificationPolicy == TransferVerificationPolicy.Size && !mustVerify)
        {
            return null;
        }

        if (destinationSession is not IStoragePortableChecksumSession checksumSession)
        {
            return mustVerify
                ? new StorageFailure(
                    "transfer.verification.hash_unavailable",
                    StorageFailureKind.Unsupported,
                    "Required destination SHA-256 verification is unavailable for this endpoint.")
                : null;
        }

        if (destination.Size is null || destination.Size != copied.BytesCopied)
        {
            return new StorageFailure(
                "transfer.verification.destination_length_mismatch",
                StorageFailureKind.Integrity,
                "The committed destination cannot be safely hashed at the transferred length.");
        }

        var result = await checksumSession.ComputePortableChecksumAsync(
            new PortableChecksumRequest(destination, copied.BytesCopied),
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return new StorageFailure(
                "transfer.verification.destination_hash_failed",
                result.Error.Kind,
                "The committed destination SHA-256 could not be verified.",
                result.Error.IsTransient,
                result.Error.ProviderCode,
                result.Error.DiagnosticId);
        }

        var expected = intent.RequiredDestinationDigest ?? copied.PortableDigest!;
        return DigestEquals(expected, result.Value.Digest)
            ? null
            : new StorageFailure(
                "transfer.verification.destination_hash_mismatch",
                StorageFailureKind.Integrity,
                "The committed destination SHA-256 did not match the transferred source bytes.");
    }

    private static async ValueTask<StorageFailure?> VerifyExpectedDestinationDigestAsync(
        PortableContentDigest expected,
        IStorageEndpointSession destinationSession,
        StorageEntry current,
        CancellationToken cancellationToken)
    {
        if (destinationSession is not IStoragePortableChecksumSession checksumSession ||
            current.Size is not long currentLength)
        {
            return new StorageFailure(
                "transfer.destination.hash_unavailable",
                StorageFailureKind.Unsupported,
                "The planned destination SHA-256 cannot be verified before overwrite.");
        }

        var result = await checksumSession.ComputePortableChecksumAsync(
            new PortableChecksumRequest(current, currentLength),
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return new StorageFailure(
                "transfer.destination.hash_failed",
                result.Error.Kind,
                "The destination SHA-256 could not be checked before overwrite.",
                result.Error.IsTransient,
                result.Error.ProviderCode,
                result.Error.DiagnosticId);
        }

        return DigestEquals(expected, result.Value.Digest)
            ? null
            : new StorageFailure(
                "transfer.destination.hash_changed",
                StorageFailureKind.Conflict,
                "The destination SHA-256 changed after the overwrite was approved.");
    }

    private static bool DigestEquals(PortableContentDigest expected, PortableContentDigest actual)
    {
        if (expected.Algorithm != actual.Algorithm)
        {
            return false;
        }

        // PortableContentDigest has already enforced an exact 64-character hexadecimal value,
        // so these allocations are fixed at 32 bytes and cannot be influenced by file size.
        var expectedBytes = Convert.FromHexString(expected.Value);
        var actualBytes = Convert.FromHexString(actual.Value);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static async ValueTask BestEffortAbortAsync(IStorageWriteHandle handle)
    {
        try
        {
            await handle.AbortAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original transfer failure remains authoritative; disposal retries cleanup.
        }
    }

    private static async ValueTask BestEffortDeleteStagingAsync(
        IStorageEndpointSession session,
        StorageEntry staging)
    {
        // Never issue an unconditional cleanup delete: after an ambiguous provider response,
        // another actor could have replaced the staging path. A later reconciliation pass can
        // safely handle providers that do not return a conditional version identity.
        if ((string.IsNullOrWhiteSpace(staging.Address.VersionId) &&
             string.IsNullOrWhiteSpace(staging.Address.EntityTag)) ||
            session.Capabilities[StorageFeature.ConditionalDelete].Level != FeatureSupportLevel.Native)
        {
            return;
        }

        try
        {
            await session.DeleteAsync(new StorageDeleteRequest(
                staging.Address,
                Recursive: false,
                IgnoreMissing: true,
                ExpectedVersionId: staging.Address.VersionId,
                ExpectedEntityTag: staging.Address.EntityTag)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original transfer failure remains authoritative. Orphaned staging entries are
            // discoverable by their reserved name and are safer than an unconditional delete.
        }
    }

    private static StorageResult<TransferExecutionReport> Fail(
        string code,
        StorageFailureKind kind,
        string message,
        bool isTransient = false) => StorageResult<TransferExecutionReport>.Fail(
        new StorageFailure(code, kind, message, isTransient));
}
