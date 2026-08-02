using System.Security.Cryptography;
using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;
using StorageHub.Security;

namespace StorageHub.Agent.Windows;

/// <summary>
/// The only agent command surface that accepts secret bytes. It is hosted exclusively on the
/// current-user secret pipe and returns opaque references or sanitized failures.
/// </summary>
public sealed class SecretVaultIpcCommandService : IAgentSecretIpcCommandHandler
{
    private readonly Func<ISecretVault> _vaultProvider;

    public SecretVaultIpcCommandService(Func<ISecretVault> vaultProvider) =>
        _vaultProvider = vaultProvider ?? throw new ArgumentNullException(nameof(vaultProvider));

    public bool CanHandle(string messageType) => messageType is
        SecretVaultIpcMessageTypes.EnrollRequest or
        SecretVaultIpcMessageTypes.UpdateRequest or
        SecretVaultIpcMessageTypes.DeleteRequest;

    public async ValueTask<AgentSecretIpcCommandResponse> HandleAsync(
        SecretIpcRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Payload is null)
        {
            return ValidationFailure(default);
        }

        var material = request.Payload.SecretMaterial;
        try
        {
            var validation = Validate(request);
            if (validation is not null)
            {
                return validation;
            }

            var vault = _vaultProvider() ?? throw new InvalidOperationException("The secret vault is unavailable.");
            return request.Payload.Operation switch
            {
                SecretVaultOperation.Enroll => await EnrollAsync(vault, request.Payload, cancellationToken)
                    .ConfigureAwait(false),
                SecretVaultOperation.Update => await UpdateAsync(vault, request.Payload, cancellationToken)
                    .ConfigureAwait(false),
                SecretVaultOperation.Delete => await DeleteAsync(vault, request.Payload, cancellationToken)
                    .ConfigureAwait(false),
                _ => ValidationFailure(request.Payload.Operation)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SecretNotFoundException)
        {
            return Failure(
                request.Payload.Operation,
                ExpectedResponseType(request.Payload.Operation),
                "secret.vault.not_found",
                StorageIpcFailureCategory.NotFound,
                "The referenced vault secret was not found.");
        }
        catch (SecretVaultCorruptedException)
        {
            return Failure(
                request.Payload.Operation,
                ExpectedResponseType(request.Payload.Operation),
                "secret.vault.integrity_failed",
                StorageIpcFailureCategory.Integrity,
                "The referenced vault entry failed its integrity check.");
        }
        catch (Exception)
        {
            return Failure(
                request.Payload.Operation,
                ExpectedResponseType(request.Payload.Operation),
                "secret.vault.unavailable",
                StorageIpcFailureCategory.Unavailable,
                "The encrypted secret vault is temporarily unavailable.",
                isTransient: true);
        }
        finally
        {
            if (material is not null)
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
    }

    private static AgentSecretIpcCommandResponse? Validate(SecretIpcRequestEnvelope request)
    {
        var payload = request.Payload;
        if (!SecretVaultIpcContract.IsSupported(payload.ContractVersion) || !payload.HasValidBounds)
        {
            return ValidationFailure(payload.Operation);
        }

        var expectedMessageType = payload.Operation switch
        {
            SecretVaultOperation.Enroll => SecretVaultIpcMessageTypes.EnrollRequest,
            SecretVaultOperation.Update => SecretVaultIpcMessageTypes.UpdateRequest,
            SecretVaultOperation.Delete => SecretVaultIpcMessageTypes.DeleteRequest,
            _ => string.Empty
        };
        return string.Equals(request.MessageType, expectedMessageType, StringComparison.Ordinal)
            ? null
            : ValidationFailure(payload.Operation);
    }

    private static async ValueTask<AgentSecretIpcCommandResponse> EnrollAsync(
        ISecretVault vault,
        SecretVaultRequest request,
        CancellationToken cancellationToken)
    {
        var written = await vault.CreateAsync(request.SecretMaterial!, cancellationToken).ConfigureAwait(false);
        return Success(request.Operation, SecretVaultIpcMessageTypes.EnrollResponse, written);
    }

    private static async ValueTask<AgentSecretIpcCommandResponse> UpdateAsync(
        ISecretVault vault,
        SecretVaultRequest request,
        CancellationToken cancellationToken)
    {
        var reference = SecretReference.Parse(request.Reference!);
        var written = await vault.RotateAsync(reference, request.SecretMaterial!, cancellationToken)
            .ConfigureAwait(false);
        return Success(request.Operation, SecretVaultIpcMessageTypes.UpdateResponse, written);
    }

    private static async ValueTask<AgentSecretIpcCommandResponse> DeleteAsync(
        ISecretVault vault,
        SecretVaultRequest request,
        CancellationToken cancellationToken)
    {
        var reference = SecretReference.Parse(request.Reference!);
        var deleted = await vault.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
        return deleted
            ? new AgentSecretIpcCommandResponse(
                SecretVaultIpcMessageTypes.DeleteResponse,
                new SecretVaultResponse(
                    SecretVaultIpcContract.CurrentVersion,
                    request.Operation,
                    Succeeded: true))
            : Failure(
                request.Operation,
                SecretVaultIpcMessageTypes.DeleteResponse,
                "secret.vault.not_found",
                StorageIpcFailureCategory.NotFound,
                "The referenced vault secret was not found.");
    }

    private static AgentSecretIpcCommandResponse Success(
        SecretVaultOperation operation,
        string messageType,
        SecretVaultWriteResult written) => new(
        messageType,
        new SecretVaultResponse(
            SecretVaultIpcContract.CurrentVersion,
            operation,
            Succeeded: true,
            written.Reference.Value,
            written.Version));

    private static AgentSecretIpcCommandResponse ValidationFailure(SecretVaultOperation operation) => Failure(
        operation,
        ExpectedResponseType(operation),
        "secret.ipc.request.invalid",
        StorageIpcFailureCategory.Validation,
        "The secret request was invalid or outside the negotiated bounds.");

    private static AgentSecretIpcCommandResponse Failure(
        SecretVaultOperation operation,
        string messageType,
        string code,
        StorageIpcFailureCategory category,
        string message,
        bool isTransient = false) => new(
        messageType,
        new SecretVaultResponse(
            SecretVaultIpcContract.CurrentVersion,
            operation,
            Succeeded: false,
            Failure: new StorageIpcFailure(code, category, message, isTransient)));

    private static string ExpectedResponseType(SecretVaultOperation operation) => operation switch
    {
        SecretVaultOperation.Enroll => SecretVaultIpcMessageTypes.EnrollResponse,
        SecretVaultOperation.Update => SecretVaultIpcMessageTypes.UpdateResponse,
        SecretVaultOperation.Delete => SecretVaultIpcMessageTypes.DeleteResponse,
        _ => SecretVaultIpcMessageTypes.ErrorResponse
    };
}
