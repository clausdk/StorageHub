using StorageHub.Domain.Identifiers;

namespace StorageHub.Domain.Tests;

public sealed class StrongIdentifierTests
{
    [Fact]
    public void Connection_profile_id_round_trips_in_canonical_form()
    {
        var value = Guid.Parse("01a61221-efc0-4dcb-a5c2-e615078a7557");
        var identifier = new ConnectionProfileId(value);

        Assert.Equal(value, identifier.Value);
        Assert.Equal("01a61221-efc0-4dcb-a5c2-e615078a7557", identifier.ToString());
        Assert.Equal(identifier, ConnectionProfileId.Parse(identifier.ToString()));
    }

    [Fact]
    public void Strong_identifiers_reject_empty_guids()
    {
        Assert.Throws<ArgumentException>(() => new ConnectionProfileId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TransferJobId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SyncProfileId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SyncRunId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new OperationPlanId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new CredentialReferenceId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new TrustRecordId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ProviderRuntimeId(Guid.Empty));
    }

    [Fact]
    public void Try_parse_rejects_missing_malformed_and_empty_values()
    {
        Assert.False(ConnectionProfileId.TryParse(null, out _));
        Assert.False(ConnectionProfileId.TryParse("not-a-guid", out _));
        Assert.False(ConnectionProfileId.TryParse(Guid.Empty.ToString(), out _));
    }
}
