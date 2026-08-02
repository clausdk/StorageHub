using StorageHub.Contracts.Results;

namespace StorageHub.Domain.Tests;

public sealed class StorageResultTests
{
    [Fact]
    public void Success_and_failure_are_unambiguous()
    {
        var success = StorageResult<int>.Success(42);
        var error = new StorageFailure(
            "storage.test.failure",
            StorageFailureKind.Conflict,
            "A test conflict occurred.",
            isTransient: false,
            providerCode: "AlreadyExists",
            diagnosticId: "diag-123");
        var failure = StorageResult<int>.Fail(error);

        Assert.True(success.IsSuccess);
        Assert.Equal(42, success.Value);
        Assert.Null(success.Error);
        Assert.True(failure.IsFailure);
        Assert.Same(error, failure.Error);
        Assert.Throws<InvalidOperationException>(() => failure.Value);
    }
}
