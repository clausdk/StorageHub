using System.Text.Json.Serialization;

namespace StorageHub.Contracts.Ipc;

/// <summary>The independently versioned, secret-only local IPC contract.</summary>
public static class SecretVaultIpcContract
{
    public const int CurrentVersion = 1;
    public const int MaximumSecretBytes = 16 * 1024 * 1024;

    public static bool IsSupported(int version) => version == CurrentVersion;
}

public static class SecretVaultIpcMessageTypes
{
    public const string EnrollRequest = "secret.vault.enroll.request";
    public const string EnrollResponse = "secret.vault.enroll.response";
    public const string UpdateRequest = "secret.vault.update.request";
    public const string UpdateResponse = "secret.vault.update.response";
    public const string DeleteRequest = "secret.vault.delete.request";
    public const string DeleteResponse = "secret.vault.delete.response";
    public const string ErrorResponse = "secret.error.response";
}

[JsonConverter(typeof(JsonStringEnumConverter<SecretMaterialPurpose>))]
public enum SecretMaterialPurpose
{
    Password = 1,
    AccessKey = 2,
    SecretAccessKey = 3,
    SessionToken = 4,
    SshPrivateKey = 5,
    SshPrivateKeyPassphrase = 6,
    ClientCertificatePfx = 7,
    ClientCertificatePassword = 8,
    ProxyCredential = 9
}

[JsonConverter(typeof(JsonStringEnumConverter<SecretVaultOperation>))]
public enum SecretVaultOperation
{
    Enroll = 1,
    Update = 2,
    Delete = 3
}

/// <summary>
/// A typed payload used only on the dedicated secret pipe. SecretMaterial is absent for delete
/// and is zeroed by both the desktop client and agent handler after each request.
/// </summary>
public sealed record SecretVaultRequest(
    int ContractVersion,
    SecretVaultOperation Operation,
    SecretMaterialPurpose Purpose,
    string? Reference,
    byte[]? SecretMaterial)
{
    public bool HasValidBounds =>
        ContractVersion > 0 &&
        Enum.IsDefined(Operation) &&
        Enum.IsDefined(Purpose) &&
        ConnectionEndpointDocument.IsOpaqueSecretReference(Reference) &&
        Operation switch
        {
            SecretVaultOperation.Enroll =>
                Reference is null && SecretMaterial is { Length: > 0 and <= SecretVaultIpcContract.MaximumSecretBytes },
            SecretVaultOperation.Update =>
                Reference is not null && SecretMaterial is { Length: > 0 and <= SecretVaultIpcContract.MaximumSecretBytes },
            SecretVaultOperation.Delete => Reference is not null && SecretMaterial is null,
            _ => false
        };
}

public sealed record SecretVaultResponse(
    int ContractVersion,
    SecretVaultOperation Operation,
    bool Succeeded,
    string? Reference = null,
    int? Version = null,
    StorageIpcFailure? Failure = null);

/// <summary>A sequence-checked request envelope used exclusively by the secret pipe.</summary>
public sealed record SecretIpcRequestEnvelope(
    string MessageType,
    Guid RequestId,
    long Sequence,
    SecretVaultRequest Payload);

/// <summary>A sequence-checked response envelope used exclusively by the secret pipe.</summary>
public sealed record SecretIpcResponseEnvelope(
    string MessageType,
    Guid RequestId,
    long Sequence,
    SecretVaultResponse Payload);
