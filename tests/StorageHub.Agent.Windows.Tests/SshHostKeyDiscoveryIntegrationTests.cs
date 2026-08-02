namespace StorageHub.Agent.Windows.Tests;

public sealed class SshHostKeyDiscoveryIntegrationTests
{
    [Fact]
    [Trait("Category", "SftpHostKeyDiscoveryIntegration")]
    public async Task DiscoveryReadsTheRealPresentedKeyWithoutCredentialsOrTrustMutation()
    {
        var portValue = Environment.GetEnvironmentVariable("STORAGEHUB_SFTP_PASSWORD_PORT");
        var expectedFingerprint = Environment.GetEnvironmentVariable("STORAGEHUB_SFTP_HOST_SHA256");
        var required = string.Equals(
            Environment.GetEnvironmentVariable("STORAGEHUB_REQUIRE_SFTP"),
            "1",
            StringComparison.Ordinal);
        if (!int.TryParse(portValue, out var port) || string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            if (required)
            {
                throw new InvalidOperationException("The required SFTP host-key discovery fixture is incomplete.");
            }

            return;
        }

        var discovery = new RenciSshHostKeyDiscovery();

        var result = await discovery.DiscoverAsync("127.0.0.1", port);
        var expectedCanonical = "SHA256:" + Convert.ToBase64String(
            Convert.FromHexString(expectedFingerprint)).TrimEnd('=');

        Assert.NotEmpty(result.HostKeyAlgorithm);
        Assert.DoesNotContain(result.HostKeyAlgorithm, char.IsWhiteSpace);
        Assert.Equal(expectedCanonical, result.Sha256Fingerprint);
    }
}
