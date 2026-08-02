namespace StorageHub.Transfers;

/// <summary>
/// Durable lifecycle states for a transfer. States that require operator or
/// recovery action are deliberately distinct from generic failure.
/// </summary>
public enum TransferState
{
    Pending = 0,
    Preparing = 1,
    Connecting = 2,
    Transferring = 3,
    Verifying = 4,
    Finalizing = 5,
    Paused = 6,
    Retrying = 7,
    BlockedCredential = 8,
    BlockedTrust = 9,
    Interrupted = 10,
    NeedsReconciliation = 11,
    RestartRequired = 12,
    CleanupPending = 13,
    Completed = 14,
    Failed = 15,
    Cancelled = 16,
}

/// <summary>
/// Persistable machine-readable status. Provider exception messages are not
/// part of durable state because they can contain credentials or URLs.
/// </summary>
public enum TransferStatusCode
{
    None = 0,
    CredentialUnavailable = 1,
    TrustRequired = 2,
    Interrupted = 3,
    StateUncertain = 4,
    ResumeNotSupported = 5,
    CleanupPending = 6,
    TransientNetworkFailure = 7,
    VerificationFailed = 8,
    DestinationChanged = 9,
    ProviderFailure = 10,
}
