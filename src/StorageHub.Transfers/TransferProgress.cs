namespace StorageHub.Transfers;

using StorageHub.Storage.Abstractions;

public readonly record struct TransferProgress(long BytesTransferred, long? TotalBytes);

public readonly record struct StreamCopyResult(
    long BytesCopied,
    PortableContentDigest? PortableDigest = null);
