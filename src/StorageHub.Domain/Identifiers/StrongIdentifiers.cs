using System.Globalization;

namespace StorageHub.Domain.Identifiers;

public readonly record struct ConnectionProfileId
{
    public ConnectionProfileId(Guid value) => Value = StrongIdentifier.Validate(value, nameof(value));
    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public static ConnectionProfileId New() => new(Guid.NewGuid());
    public static ConnectionProfileId Parse(string value) => new(StrongIdentifier.Parse(value));
    public static bool TryParse(string? value, out ConnectionProfileId result)
    {
        var success = StrongIdentifier.TryParse(value, out var parsed);
        result = success ? new ConnectionProfileId(parsed) : default;
        return success;
    }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct TransferJobId
{
    public TransferJobId(Guid value) => Value = StrongIdentifier.Validate(value, nameof(value));
    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public static TransferJobId New() => new(Guid.NewGuid());
    public static TransferJobId Parse(string value) => new(StrongIdentifier.Parse(value));
    public static bool TryParse(string? value, out TransferJobId result)
    {
        var success = StrongIdentifier.TryParse(value, out var parsed);
        result = success ? new TransferJobId(parsed) : default;
        return success;
    }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct SyncProfileId
{
    public SyncProfileId(Guid value) => Value = StrongIdentifier.Validate(value, nameof(value));
    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public static SyncProfileId New() => new(Guid.NewGuid());
    public static SyncProfileId Parse(string value) => new(StrongIdentifier.Parse(value));
    public static bool TryParse(string? value, out SyncProfileId result)
    {
        var success = StrongIdentifier.TryParse(value, out var parsed);
        result = success ? new SyncProfileId(parsed) : default;
        return success;
    }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct SyncRunId
{
    public SyncRunId(Guid value) => Value = StrongIdentifier.Validate(value, nameof(value));
    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public static SyncRunId New() => new(Guid.NewGuid());
    public static SyncRunId Parse(string value) => new(StrongIdentifier.Parse(value));
    public static bool TryParse(string? value, out SyncRunId result)
    {
        var success = StrongIdentifier.TryParse(value, out var parsed);
        result = success ? new SyncRunId(parsed) : default;
        return success;
    }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct OperationPlanId
{
    public OperationPlanId(Guid value) => Value = StrongIdentifier.Validate(value, nameof(value));
    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public static OperationPlanId New() => new(Guid.NewGuid());
    public static OperationPlanId Parse(string value) => new(StrongIdentifier.Parse(value));
    public static bool TryParse(string? value, out OperationPlanId result)
    {
        var success = StrongIdentifier.TryParse(value, out var parsed);
        result = success ? new OperationPlanId(parsed) : default;
        return success;
    }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct CredentialReferenceId
{
    public CredentialReferenceId(Guid value) => Value = StrongIdentifier.Validate(value, nameof(value));
    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public static CredentialReferenceId New() => new(Guid.NewGuid());
    public static CredentialReferenceId Parse(string value) => new(StrongIdentifier.Parse(value));
    public static bool TryParse(string? value, out CredentialReferenceId result)
    {
        var success = StrongIdentifier.TryParse(value, out var parsed);
        result = success ? new CredentialReferenceId(parsed) : default;
        return success;
    }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct TrustRecordId
{
    public TrustRecordId(Guid value) => Value = StrongIdentifier.Validate(value, nameof(value));
    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public static TrustRecordId New() => new(Guid.NewGuid());
    public static TrustRecordId Parse(string value) => new(StrongIdentifier.Parse(value));
    public static bool TryParse(string? value, out TrustRecordId result)
    {
        var success = StrongIdentifier.TryParse(value, out var parsed);
        result = success ? new TrustRecordId(parsed) : default;
        return success;
    }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

public readonly record struct ProviderRuntimeId
{
    public ProviderRuntimeId(Guid value) => Value = StrongIdentifier.Validate(value, nameof(value));
    public Guid Value { get; }
    public bool IsEmpty => Value == Guid.Empty;
    public static ProviderRuntimeId New() => new(Guid.NewGuid());
    public static ProviderRuntimeId Parse(string value) => new(StrongIdentifier.Parse(value));
    public static bool TryParse(string? value, out ProviderRuntimeId result)
    {
        var success = StrongIdentifier.TryParse(value, out var parsed);
        result = success ? new ProviderRuntimeId(parsed) : default;
        return success;
    }
    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

internal static class StrongIdentifier
{
    public static Guid Validate(Guid value, string parameterName) => value != Guid.Empty
        ? value
        : throw new ArgumentException("A strongly typed identifier cannot be empty.", parameterName);

    public static Guid Parse(string value)
    {
        if (!TryParse(value, out var parsed))
        {
            throw new FormatException("The value is not a non-empty GUID identifier.");
        }

        return parsed;
    }

    public static bool TryParse(string? value, out Guid parsed) =>
        Guid.TryParse(value, out parsed) && parsed != Guid.Empty;
}
