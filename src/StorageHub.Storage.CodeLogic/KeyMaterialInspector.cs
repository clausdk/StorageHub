using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Renci.SshNet;
using Renci.SshNet.Common;
using StorageHub.Application.Connections;
using StorageHub.Application.Credentials;
using StorageHub.Contracts.Results;

namespace StorageHub.Storage.CodeLogic;

/// <summary>
/// Derives the non-sensitive summary that the key store displays, from material that has just been
/// imported. This runs in the agent only. It is deliberately the single place that opens key
/// material for inspection: nothing here returns bytes, and every failure is reduced to a safe
/// structured result so that a wrong password cannot surface as a provider exception message.
/// </summary>
public static class KeyMaterialInspector
{
    /// <summary>Bounds inspection to the same ceiling the secret pipe accepts.</summary>
    public const int MaximumMaterialBytes = 16 * 1024 * 1024;

    public static StorageResult<KeyMaterialSummary> InspectPkcs12(
        ReadOnlySpan<byte> material,
        string password)
    {
        if (material.IsEmpty || material.Length > MaximumMaterialBytes)
        {
            return Invalid("keystore.material.invalid", "The certificate file is empty or too large.");
        }

        X509Certificate2Collection? loaded = null;
        try
        {
            // EphemeralKeySet keeps nothing in the user's certificate store or on disk. The
            // collection form is used so that a bundle carrying a chain reports its real length.
            loaded = X509CertificateLoader.LoadPkcs12Collection(
                material,
                password,
                X509KeyStorageFlags.EphemeralKeySet);
            if (loaded.Count == 0)
            {
                return Invalid("keystore.material.invalid", "The certificate file contains no certificate.");
            }

            var leaf = SelectLeaf(loaded);
            using var key = leaf.GetRSAPrivateKey() as AsymmetricAlgorithm ??
                leaf.GetECDsaPrivateKey() as AsymmetricAlgorithm;
            return StorageResult<KeyMaterialSummary>.Success(new Pkcs12CertificateSummary(
                Describe(leaf.Subject, "(no subject)"),
                Describe(leaf.Issuer, "(no issuer)"),
                leaf.NotBefore,
                leaf.NotAfter,
                Convert.ToHexString(SHA256.HashData(leaf.RawDataMemory.Span)),
                leaf.PublicKey.Oid.FriendlyName ?? leaf.PublicKey.Oid.Value ?? "Unknown",
                key?.KeySize ?? 0,
                leaf.HasPrivateKey,
                loaded.Count));
        }
        catch (CryptographicException)
        {
            // Covers a wrong password and a malformed container alike; the distinction is not
            // reported because it would confirm password correctness to a caller.
            return Invalid(
                "keystore.material.unreadable",
                "The certificate could not be read. Check the file and its password.");
        }
        finally
        {
            if (loaded is not null)
            {
                foreach (var certificate in loaded)
                {
                    certificate.Dispose();
                }
            }
        }
    }

    public static StorageResult<KeyMaterialSummary> InspectSshPrivateKey(
        ReadOnlyMemory<byte> material,
        string passphrase,
        SftpPrivateKeyFormat format)
    {
        if (material.IsEmpty || material.Length > MaximumMaterialBytes)
        {
            return Invalid("keystore.material.invalid", "The private key file is empty or too large.");
        }

        if (!Enum.IsDefined(format))
        {
            return Invalid("keystore.material.invalid", "The private key format is not recognized.");
        }

        // Reuse the existing envelope validator rather than reimplementing it: it is the same check
        // the SFTP connector applies, so a key the store accepts is a key that can actually connect.
        var envelope = PrivateKeyEncryptionValidator.Validate(material.Span, passphrase, format);
        if (envelope == PrivateKeyValidationResult.Unencrypted)
        {
            return Invalid(
                "keystore.material.unprotected",
                "The private key is not passphrase protected. StorageHub requires an encrypted key.");
        }

        if (envelope != PrivateKeyValidationResult.Valid)
        {
            return Invalid(
                "keystore.material.unreadable",
                "The private key could not be read. Check the file, its format, and its passphrase.");
        }

        try
        {
            using var stream = new MemoryStream(material.ToArray(), writable: false);
            using var key = new PrivateKeyFile(stream, passphrase);
            var algorithm = key.HostKeyAlgorithms.FirstOrDefault();
            if (algorithm is null)
            {
                return Invalid("keystore.material.unreadable", "The private key exposes no usable algorithm.");
            }

            return StorageResult<KeyMaterialSummary>.Success(new SshPrivateKeySummary(
                format,
                algorithm.Name,
                "SHA256:" + Convert.ToBase64String(SHA256.HashData(algorithm.Data)).TrimEnd('='),
                comment: null));
        }
        catch (Exception error) when (error is SshException or CryptographicException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return Invalid(
                "keystore.material.unreadable",
                "The private key could not be read. Check the file, its format, and its passphrase.");
        }
    }

    /// <summary>
    /// Picks the certificate a user means by "the certificate": the one holding the private key,
    /// falling back to the first entry when a bundle carries only public certificates.
    /// </summary>
    private static X509Certificate2 SelectLeaf(X509Certificate2Collection loaded)
    {
        foreach (var certificate in loaded)
        {
            if (certificate.HasPrivateKey)
            {
                return certificate;
            }
        }

        return loaded[0];
    }

    private static string Describe(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static StorageResult<KeyMaterialSummary> Invalid(string code, string message) =>
        StorageResult<KeyMaterialSummary>.Fail(new StorageFailure(code, StorageFailureKind.Validation, message));
}
