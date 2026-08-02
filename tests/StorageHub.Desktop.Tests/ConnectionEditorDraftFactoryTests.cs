using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop.Tests;

public sealed class ConnectionEditorDraftFactoryTests
{
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
            ["privateKeyPassphraseReference"] = "shs_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
        };

        var draft = ConnectionEditorDraftFactory.Build(StorageProviderKind.Sftp, values);

        Assert.True(draft.HasValidBounds);
        Assert.Equal(StorageConnectionProvider.Sftp, draft.Endpoint.Provider);
        Assert.Equal(ConnectionAuthenticationKind.SftpPrivateKey, draft.Authentication.Kind);
        Assert.Equal(values["privateKeyReference"], draft.Authentication.PrivateKeyReference);
        Assert.Equal(values["privateKeyPassphraseReference"], draft.Authentication.PrivateKeyPassphraseReference);
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
}
