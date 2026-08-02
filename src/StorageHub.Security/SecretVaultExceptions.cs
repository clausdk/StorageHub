namespace StorageHub.Security;

public class SecretVaultException : Exception
{
    public SecretVaultException(string message)
        : base(message)
    {
    }
}

public sealed class SecretNotFoundException(SecretReference reference) : SecretVaultException(
    $"Secret reference '{reference}' was not found.");

public sealed class SecretVaultCorruptedException(SecretReference reference) : SecretVaultException(
    $"Secret reference '{reference}' could not be authenticated or has a damaged envelope.");
