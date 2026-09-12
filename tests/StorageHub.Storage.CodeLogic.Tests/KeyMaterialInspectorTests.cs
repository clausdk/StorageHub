using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StorageHub.Application.Connections;
using StorageHub.Application.Credentials;
using StorageHub.Contracts.Results;

namespace StorageHub.Storage.CodeLogic.Tests;

/// <summary>
/// Material is generated in-process rather than committed, so the suite never carries a private key
/// on disk and the tests stay hermetic.
/// </summary>
public sealed class KeyMaterialInspectorTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public void DescribesAPkcs12BundleWithoutReturningMaterial()
    {
        var (material, certificate) = CreateCertificate("CN=StorageHub Test Leaf");
        using var expected = certificate;

        var result = KeyMaterialInspector.InspectPkcs12(material, Password);

        Assert.True(result.IsSuccess);
        var summary = Assert.IsType<Pkcs12CertificateSummary>(result.Value);
        Assert.Equal("CN=StorageHub Test Leaf", summary.Subject);
        Assert.Equal("CN=StorageHub Test Leaf", summary.Issuer);
        Assert.True(summary.HasPrivateKey);
        Assert.Equal(2048, summary.KeySizeBits);
        Assert.Equal(1, summary.ChainLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected.RawData)), summary.Sha256Thumbprint);
        Assert.False(summary.IsExpiredAt(DateTimeOffset.UtcNow));
        Assert.False(summary.IsNotYetValidAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ReportsExpiryWindowsSoTheStoreCanWarnBeforeUse()
    {
        var (material, certificate) = CreateCertificate(
            "CN=Expired",
            notBefore: DateTimeOffset.UtcNow.AddDays(-10),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        certificate.Dispose();

        var summary = Assert.IsType<Pkcs12CertificateSummary>(
            KeyMaterialInspector.InspectPkcs12(material, Password).Value);

        Assert.True(summary.IsExpiredAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DescribesAPasswordLessCertificate()
    {
        // A PKCS#12 bundle may legitimately carry no password; PKCS#12 treats empty as "none".
        var (material, certificate) = CreateCertificate("CN=No Password", password: "");
        certificate.Dispose();

        var result = KeyMaterialInspector.InspectPkcs12(material, string.Empty);

        Assert.True(result.IsSuccess);
        var summary = Assert.IsType<Pkcs12CertificateSummary>(result.Value);
        Assert.Equal("CN=No Password", summary.Subject);
        Assert.True(summary.HasPrivateKey);
    }

    [Fact]
    public void RejectsAWrongCertificatePasswordWithoutConfirmingIt()
    {
        var (material, certificate) = CreateCertificate("CN=StorageHub Test Leaf");
        certificate.Dispose();

        var result = KeyMaterialInspector.InspectPkcs12(material, "not the password");

        Assert.True(result.IsFailure);
        Assert.Equal(StorageFailureKind.Validation, result.Error.Kind);
        // The message must not distinguish a wrong password from a malformed container.
        Assert.DoesNotContain("password is", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incorrect", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void RejectsMaterialThatIsNotACertificateContainer(int length)
    {
        var result = KeyMaterialInspector.InspectPkcs12(new byte[length], Password);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void DescribesAnEncryptedPkcs8PrivateKey()
    {
        var key = CreateEncryptedPkcs8Key();

        var result = KeyMaterialInspector.InspectSshPrivateKey(key, Password, SftpPrivateKeyFormat.Pkcs8);

        Assert.True(result.IsSuccess);
        var summary = Assert.IsType<SshPrivateKeySummary>(result.Value);
        Assert.Equal(SftpPrivateKeyFormat.Pkcs8, summary.Format);
        Assert.StartsWith("SHA256:", summary.Sha256Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain('=', summary.Sha256Fingerprint);
        Assert.False(string.IsNullOrWhiteSpace(summary.PublicKeyAlgorithm));
    }

    [Fact]
    public void RefusesAPrivateKeyThatCarriesNoPassphrase()
    {
        // The SFTP connector rejects unprotected keys outright, so the store must not accept one
        // it could never use.
        using var rsa = RSA.Create(2048);
        var pem = Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem());

        var result = KeyMaterialInspector.InspectSshPrivateKey(pem, Password, SftpPrivateKeyFormat.Pkcs8);

        Assert.True(result.IsFailure);
        Assert.Equal("keystore.material.unprotected", result.Error.Code);
    }

    [Fact]
    public void RejectsAWrongPassphraseWithoutConfirmingIt()
    {
        var key = CreateEncryptedPkcs8Key();

        var result = KeyMaterialInspector.InspectSshPrivateKey(
            key,
            "not the passphrase",
            SftpPrivateKeyFormat.Pkcs8);

        Assert.True(result.IsFailure);
        Assert.Equal("keystore.material.unreadable", result.Error.Code);
    }

    [Fact]
    public void RejectsAnUndefinedPrivateKeyFormat()
    {
        var result = KeyMaterialInspector.InspectSshPrivateKey(
            CreateEncryptedPkcs8Key(),
            Password,
            (SftpPrivateKeyFormat)99);

        Assert.True(result.IsFailure);
    }

    private static byte[] CreateEncryptedPkcs8Key()
    {
        using var rsa = RSA.Create(2048);
        return Encoding.ASCII.GetBytes(rsa.ExportEncryptedPkcs8PrivateKeyPem(
            Password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
    }

    private static (byte[] Material, X509Certificate2 Certificate) CreateCertificate(
        string subject,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string? password = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(2));
        return (
            certificate.Export(X509ContentType.Pkcs12, password ?? Password),
            X509CertificateLoader.LoadCertificate(certificate.RawData));
    }
}
