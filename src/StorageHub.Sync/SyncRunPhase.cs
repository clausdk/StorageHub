namespace StorageHub.Sync;

public enum SyncRunPhase
{
    Pending = 0,
    Scanning = 1,
    Planning = 2,
    AwaitingApproval = 3,
    Ready = 4,
    Executing = 5,
    Verifying = 6,
    CommittingBaseline = 7,
    BlockedConflict = 8,
    BlockedDeletionGuard = 9,
    BlockedEndpoint = 10,
    BlockedCredential = 11,
    BlockedTrust = 12,
    Interrupted = 13,
    NeedsReconciliation = 14,
    Completed = 15,
    Failed = 16,
    Cancelled = 17,
}

public enum SyncStatusCode
{
    None = 0,
    ConflictRequiresDecision = 1,
    DeletionGuardTriggered = 2,
    EndpointUnavailable = 3,
    CredentialUnavailable = 4,
    TrustRequired = 5,
    Interrupted = 6,
    StateUncertain = 7,
    VerificationFailed = 8,
    ProviderFailure = 9,
}
