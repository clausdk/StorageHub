using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class RemoteBrowserErrorsTests
{
    [Fact]
    public void CredentialRejectionIsNotReportedAsTrustFailure()
    {
        var failure = Failure(StorageIpcFailureCategory.Unauthorized, "storage.unauthorized");

        var message = RemoteBrowserErrors.ForFailure(failure);

        Assert.Contains("credential", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trust", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrustFailureIsNotReportedAsCredentialRejection()
    {
        var failure = Failure(StorageIpcFailureCategory.Security, "storage.security");

        var message = RemoteBrowserErrors.ForFailure(failure);

        Assert.Contains("trust", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", message, StringComparison.OrdinalIgnoreCase);
    }

    private static StorageIpcFailure Failure(StorageIpcFailureCategory category, string code) =>
        new(code, category, "Sanitized agent message.", IsTransient: false);
}
