using StorageHub.Agent.Ipc;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class PresentationModelTests
{
    [Fact]
    public void CoreProviderCatalogSeparatesStorageProvidersFromClientProviders()
    {
        Assert.Equal(
            [
                StorageProviderKind.Local,
                StorageProviderKind.S3,
                StorageProviderKind.Ftp,
                StorageProviderKind.Ftps,
                StorageProviderKind.Sftp,
                StorageProviderKind.Ssh
            ],
            ConnectionProviderCatalog.All.Select(provider => provider.Kind));

        Assert.Equal(443, ConnectionProviderCatalog.Get(StorageProviderKind.S3).DefaultPort);
        Assert.Equal(21, ConnectionProviderCatalog.Get(StorageProviderKind.Ftp).DefaultPort);
        Assert.Equal(21, ConnectionProviderCatalog.Get(StorageProviderKind.Ftps).DefaultPort);
        Assert.Equal(22, ConnectionProviderCatalog.Get(StorageProviderKind.Sftp).DefaultPort);
        var ssh = ConnectionProviderCatalog.Get(StorageProviderKind.Ssh);
        Assert.Equal(22, ssh.DefaultPort);
        Assert.Equal(ConnectionProfileType.Client, ssh.Type);
        Assert.All(
            ConnectionProviderCatalog.All.Where(static provider => provider.Kind != StorageProviderKind.Ssh),
            static provider => Assert.Equal(ConnectionProfileType.Storage, provider.Type));
        Assert.Contains("no PuTTY", ssh.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderSecretsAreAlwaysRepresentedAsReferences()
    {
        var secretFields = ConnectionProviderCatalog.All
            .SelectMany(provider => provider.AuthenticationFields.Concat(provider.SecurityFields))
            .Where(field => field.Kind is ConnectionFieldKind.SecretReference or ConnectionFieldKind.CertificateReference)
            .ToArray();

        Assert.NotEmpty(secretFields);
        Assert.All(secretFields, field =>
        {
            Assert.EndsWith("Reference", field.Key, StringComparison.Ordinal);
            Assert.Contains("reference", field.Label, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ProviderTrustPoliciesAreExplicitAndSafeByDefault()
    {
        var ftp = ConnectionProviderCatalog.Get(StorageProviderKind.Ftp);
        var ftps = ConnectionProviderCatalog.Get(StorageProviderKind.Ftps);
        var sftp = ConnectionProviderCatalog.Get(StorageProviderKind.Sftp);
        var s3 = ConnectionProviderCatalog.Get(StorageProviderKind.S3);

        Assert.False(ftp.EncryptedByDefault);
        Assert.Contains("without transport encryption", ftp.TrustNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hostname", ftps.TrustNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never accepted silently", sftp.TrustNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sftp.SecurityFields, field => field.Key == "hostKeyFingerprint" && field.Required);
        Assert.Contains("system TLS", s3.TrustNotice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(s3.SecurityFields, field =>
            field.Key is "clientCertificateReference" or "certificatePin");
        Assert.Contains(s3.AuthenticationFields, field =>
            field.Key == "accessKeyReference" && field.Kind == ConnectionFieldKind.SecretReference);
        Assert.Contains("no PuTTY dependency", sftp.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderEditorsOnlyOfferOptionsRepresentedByRuntimeProfiles()
    {
        var local = ConnectionProviderCatalog.Get(StorageProviderKind.Local);
        var s3 = ConnectionProviderCatalog.Get(StorageProviderKind.S3);
        var ftp = ConnectionProviderCatalog.Get(StorageProviderKind.Ftp);
        var ftps = ConnectionProviderCatalog.Get(StorageProviderKind.Ftps);
        var sftp = ConnectionProviderCatalog.Get(StorageProviderKind.Sftp);
        var ssh = ConnectionProviderCatalog.Get(StorageProviderKind.Ssh);

        Assert.Equal(["rootPath"], local.GeneralFields.Select(field => field.Key));
        Assert.Empty(local.AuthenticationFields);
        Assert.Empty(local.SecurityFields);
        Assert.Empty(s3.SecurityFields);
        Assert.DoesNotContain(ftp.GeneralFields, field => field.Key == "passiveMode");
        Assert.DoesNotContain(ftps.GeneralFields, field => field.Key == "passiveMode");
        Assert.DoesNotContain(sftp.GeneralFields, field => field.Key == "keepAliveSeconds");
        Assert.DoesNotContain(sftp.SecurityFields, field => field.Key == "hostKeyPolicy");
        var authentication = Assert.Single(sftp.AuthenticationFields, field => field.Key == "authenticationMode");
        Assert.Equal(["Private key reference", "Password reference"], authentication.Choices);
        var sshAuthentication = Assert.Single(
            ssh.AuthenticationFields,
            field => field.Key == "authenticationMode");
        Assert.Equal(
            ["Private key reference", "Password reference", "Private key + password (MFA)"],
            sshAuthentication.Choices);
    }

    [Fact]
    public void QuickConnectValidationRejectsMissingEndpointsAndWarnsAboutPlainFtp()
    {
        var missingSftp = new QuickConnectDraft(StorageProviderKind.Sftp, "", 22, "user", true);
        var plainFtp = new QuickConnectDraft(StorageProviderKind.Ftp, "ftp.example.com", 21, "user", false);
        var validLocal = new QuickConnectDraft(StorageProviderKind.Local, @"C:\Data", null, "", true);

        Assert.Contains(missingSftp.Validate(), error => error.Contains("host", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plainFtp.Validate(), error => error.Contains("unencrypted", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(validLocal.Validate());
    }

    [Fact]
    public void ShellStatusProducesCompactAccessibleText()
    {
        var status = new ShellStatusSnapshot(
            "S3 Archive / releases",
            3,
            1536,
            10 * 1024 * 1024,
            7,
            2,
            AgentConnectionState.Connected);

        Assert.Contains("3", status.SelectionText, StringComparison.Ordinal);
        Assert.Contains("KiB", status.SelectionText, StringComparison.Ordinal);
        Assert.Equal("10 MiB/s", status.TransferRateText);
        Assert.Contains("Active: 2", status.QueueText, StringComparison.Ordinal);
        Assert.Equal("Agent: connected", status.AgentText);
    }

    [Fact]
    public void SyncDefaultsRequirePreviewAndKeepDeletesDisabled()
    {
        Assert.False(SyncPresentationCatalog.DeletePropagationEnabledByDefault);
        Assert.Equal(100, SyncPresentationCatalog.DefaultMassDeleteItemLimit);
        Assert.Equal(10m, SyncPresentationCatalog.DefaultMassDeletePercentageLimit);
        Assert.All(
            SyncPresentationCatalog.AllModes.Where(mode => mode.Kind != SyncModeKind.CompareOnly),
            mode => Assert.True(mode.RequiresPreview));
        Assert.False(SyncPresentationCatalog.AllModes.Single(mode => mode.Kind == SyncModeKind.BackupLeftToRight).CanPropagateDeletes);
    }

    [Fact]
    public void AgentMonitorUsesBoundedNonBlockingDefaults()
    {
        Assert.Equal(StorageHubIpcPipeNames.Normal, AgentStatusMonitor.DefaultPipeName);
        Assert.Equal(StorageHubIpcPipeNames.Normal, new RemoteStorageAgentClientOptions().PipeName);
        Assert.Equal(StorageHubIpcPipeNames.Normal, new ObjectInspectorAgentClientOptions().PipeName);
        Assert.Equal(StorageHubIpcPipeNames.Normal, new SyncManagementAgentClientOptions().PipeName);
        Assert.Equal(StorageHubIpcPipeNames.Normal, new ScheduleManagementAgentClientOptions().PipeName);
        Assert.Equal(StorageHubIpcPipeNames.Secret, new RemoteSecretVaultClientOptions().PipeName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentStatusMonitor(connectTimeout: TimeSpan.FromSeconds(6)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentStatusMonitor(pollInterval: TimeSpan.FromMilliseconds(500)));
    }
}
