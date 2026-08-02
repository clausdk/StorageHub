using System.Security.Cryptography;

namespace StorageHub.Security;

public readonly record struct SecretReference
{
    private const string Prefix = "shs_";
    private const int EncodedLength = 43;

    private SecretReference(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static SecretReference Create()
    {
        Span<byte> random = stackalloc byte[32];
        try
        {
            RandomNumberGenerator.Fill(random);
            var encoded = Convert.ToBase64String(random)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return new SecretReference(Prefix + encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    public static SecretReference Parse(string value)
    {
        if (!TryParse(value, out var reference))
        {
            throw new FormatException("The secret reference is not a valid opaque StorageHub reference.");
        }

        return reference;
    }

    public static bool TryParse(string? value, out SecretReference reference)
    {
        reference = default;
        if (value is null || value.Length != Prefix.Length + EncodedLength ||
            !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(Prefix.Length))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                return false;
            }
        }

        reference = new SecretReference(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;
}
