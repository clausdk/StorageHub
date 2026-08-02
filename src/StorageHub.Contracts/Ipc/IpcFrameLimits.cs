namespace StorageHub.Contracts.Ipc;

public enum IpcFrameKind
{
    Normal,
    Secret
}

/// <summary>Hard limits applied before allocating or parsing an IPC frame.</summary>
public static class IpcFrameLimits
{
    public const int NormalMaxBytes = 8 * 1024 * 1024;
    public const int SecretMaxBytes = 32 * 1024 * 1024;

    public static int GetMaximumBytes(IpcFrameKind kind) => kind switch
    {
        IpcFrameKind.Normal => NormalMaxBytes,
        IpcFrameKind.Secret => SecretMaxBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown IPC frame kind.")
    };

    public static bool IsAllowed(long frameLength, IpcFrameKind kind) =>
        frameLength >= 0 && frameLength <= GetMaximumBytes(kind);
}
