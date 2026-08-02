namespace StorageHub.Contracts.Results;

/// <summary>Classifies a failure without coupling callers to provider exception types.</summary>
public enum StorageFailureKind
{
    Validation,
    NotFound,
    Conflict,
    Unsupported,
    Unauthorized,
    Unavailable,
    Timeout,
    Cancelled,
    Integrity,
    Security,
    Provider,
    Unexpected
}

/// <summary>A safe, structured description of an expected storage failure.</summary>
public sealed record StorageFailure
{
    public StorageFailure(
        string code,
        StorageFailureKind kind,
        string message,
        bool isTransient = false,
        string? providerCode = null,
        string? diagnosticId = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A stable failure code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A safe failure message is required.", nameof(message));
        }

        Code = code;
        Kind = kind;
        Message = message;
        IsTransient = isTransient;
        ProviderCode = NormalizeOptional(providerCode);
        DiagnosticId = NormalizeOptional(diagnosticId);
    }

    public string Code { get; }

    public StorageFailureKind Kind { get; }

    public string Message { get; }

    public bool IsTransient { get; }

    /// <summary>A non-secret provider error code, when one is safe to expose.</summary>
    public string? ProviderCode { get; }

    /// <summary>An opaque correlation identifier for privileged diagnostics.</summary>
    public string? DiagnosticId { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
