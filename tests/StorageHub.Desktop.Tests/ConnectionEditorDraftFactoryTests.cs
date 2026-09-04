using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ConnectionEditorDraftFactoryTests
{
    [Theory]
    [InlineData(StorageProviderKind.S3, 30, 3)]
    [InlineData(StorageProviderKind.Ftp, 30, 0)]
    [InlineData(StorageProviderKind.Ftps, 30, 0)]
    [InlineData(StorageProviderKind.Sftp, 30, 0)]
    [InlineData(StorageProviderKind.Ssh, 30, 0)]
    public void RemoteDraftsUseOnlyOperationalDefaultsTheirProviderCanEnforce(
        StorageProviderKind provider,
        int operationTimeoutSeconds,
        int maximumRetryAttempts)
    {
        var draft = ConnectionEditorDraftFactory.Build(provider, ValidValues(provider));

        Assert.Equal(30, draft.OperationalOptions.ConnectTimeoutSeconds);
        Assert.Equal(operationTimeoutSeconds, draft.OperationalOptions.OperationTimeoutSeconds);
        Assert.Equal(maximumRetryAttempts, draft.OperationalOptions.MaximumRetryAttempts);
    }

    [Fact]
    public void BuildsSftpPrivateKeyDraftUsingOnlyOpaqueReferences()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "Production SFTP",
            ["host"] = "sftp.example.com",
            ["port"] = "22",
            ["initialPath"] = "/exports",
            ["username"] = "backup",
            ["authenticationMode"] = "Private key reference",
            ["privateKeyReference"] = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["privateKeyPassphraseReference"] = "shs_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            ["hostKeyFingerprint"] = new string('A', 64)
        };

        var draft = ConnectionEditorDraftFactory.Build(StorageProviderKind.Sftp, values);

        Assert.True(draft.HasValidBounds);
        Assert.Equal(StorageConnectionProvider.Sftp, draft.Endpoint.Provider);
        Assert.Equal(ConnectionAuthenticationKind.SftpPrivateKey, draft.Authentication.Kind);
        Assert.Equal(values["privateKeyReference"], draft.Authentication.PrivateKeyReference);
        Assert.Equal(values["privateKeyPassphraseReference"], draft.Authentication.PrivateKeyPassphraseReference);
    }

    [Fact]
    public void BuildsSshClientDraftWithLabelsAndClientType()
    {
        var values = ValidValues(StorageProviderKind.Ssh);
        values["folder"] = "Operations";
        values["labels"] = "production, linux, production";

        var draft = ConnectionEditorDraftFactory.Build(StorageProviderKind.Ssh, values);

        Assert.Equal(ConnectionProfileType.Client, draft.Type);
        Assert.Equal(StorageConnectionProvider.Ssh, draft.Endpoint.Provider);
        Assert.Equal("Operations", draft.Metadata.FolderPath);
        Assert.Equal(["production", "linux"], Assert.IsType<string[]>(draft.Metadata.Tags));
    }

    [Fact]
    public void BuildsSshPrivateKeyAndPasswordMfaDraft()
    {
        var values = ValidValues(StorageProviderKind.Ssh);
        values["authenticationMode"] = "Private key + password (MFA)";
        values["privateKeyReference"] = "shs_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        values["privateKeyPassphraseReference"] = "shs_CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

        var draft = ConnectionEditorDraftFactory.Build(StorageProviderKind.Ssh, values);

        Assert.True(draft.HasValidBounds);
        Assert.Equal(ConnectionAuthenticationKind.SshPrivateKeyPassword, draft.Authentication.Kind);
        Assert.NotNull(draft.Authentication.PasswordReference);
        Assert.NotNull(draft.Authentication.PrivateKeyReference);
        Assert.NotNull(draft.Authentication.PrivateKeyPassphraseReference);
        Assert.DoesNotContain(
            "Private key + password (MFA)",
            ConnectionProviderCatalog.Get(StorageProviderKind.Sftp)
                .AuthenticationFields.Single(field => field.Key == "authenticationMode").Choices!);
    }

    [Fact]
    public void PlainFtpRequiresExplicitAcknowledgement()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "Legacy FTP",
            ["host"] = "ftp.example.com",
            ["port"] = "21",
            ["username"] = "backup",
            ["passwordReference"] = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["acknowledgePlaintext"] = "false"
        };

        var error = Assert.Throws<ArgumentException>(() =>
            ConnectionEditorDraftFactory.Build(StorageProviderKind.Ftp, values));

        Assert.Contains("acknowledgement", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MD5:unsafe")]
    [InlineData("AAAAAAAA AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void PinnedSftpRequiresStrictVerifiedSha256Fingerprint(string fingerprint)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "SFTP",
            ["host"] = "sftp.example.test",
            ["port"] = "22",
            ["username"] = "operator",
            ["authenticationMode"] = "Password reference",
            ["passwordReference"] = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["hostKeyFingerprint"] = fingerprint
        };

        Assert.Throws<ArgumentException>(() =>
            ConnectionEditorDraftFactory.Build(StorageProviderKind.Sftp, values));
    }

    [Fact]
    public void PinnedFtpsRequiresCertificateFingerprintBeforeProfileCanBeSaved()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "FTPS",
            ["host"] = "ftps.example.test",
            ["port"] = "21",
            ["username"] = "operator",
            ["passwordReference"] = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["trustMode"] = "System trust + certificate pin"
        };

        Assert.Throws<ArgumentException>(() =>
            ConnectionEditorDraftFactory.Build(StorageProviderKind.Ftps, values));
    }

    private static Dictionary<string, string> ValidValues(StorageProviderKind provider) => provider switch
    {
        StorageProviderKind.S3 => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "S3",
            ["endpoint"] = "https://s3.amazonaws.com",
            ["bucket"] = "archive",
            ["region"] = "eu-north-1",
            ["authenticationMode"] = "Default credential chain"
        },
        StorageProviderKind.Ftp => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "FTP",
            ["host"] = "ftp.example.test",
            ["port"] = "21",
            ["username"] = "operator",
            ["passwordReference"] = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["acknowledgePlaintext"] = "true"
        },
        StorageProviderKind.Ftps => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "FTPS",
            ["host"] = "ftps.example.test",
            ["port"] = "21",
            ["username"] = "operator",
            ["passwordReference"] = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["trustMode"] = "System trust"
        },
        StorageProviderKind.Sftp => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "SFTP",
            ["host"] = "sftp.example.test",
            ["port"] = "22",
            ["username"] = "operator",
            ["authenticationMode"] = "Password reference",
            ["passwordReference"] = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["hostKeyFingerprint"] = new string('A', 64)
        },
        StorageProviderKind.Ssh => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profileName"] = "SSH shell",
            ["host"] = "ssh.example.test",
            ["port"] = "22",
            ["username"] = "operator",
            ["authenticationMode"] = "Password reference",
            ["passwordReference"] = "shs_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["hostKeyFingerprint"] = new string('A', 64)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}
