using System.Globalization;
using StorageHub.Contracts.Ipc;

namespace StorageHub.Desktop;

public sealed record ConnectionProviderDefaults(
    int ConnectTimeoutSeconds,
    int OperationTimeoutSeconds,
    int MaximumRetryAttempts,
    IReadOnlyDictionary<string, string> FieldValues);

internal static class ConnectionDefaultSettings
{
    internal const string ConnectTimeoutKey = "connectTimeoutSeconds";
    internal const string OperationTimeoutKey = "operationTimeoutSeconds";
    internal const string RetryAttemptsKey = "maximumRetryAttempts";

    private static readonly HashSet<string> EditableFieldKeys = new(StringComparer.Ordinal)
    {
        "port",
        "initialPath",
        "s3ServiceType",
        "endpoint",
        "region",
        "prefix",
        "addressingStyle",
        "tlsMode",
        "trustMode",
        "authenticationMode",
        "privateKeyReference"
    };

    internal static IReadOnlyList<ConnectionFieldDescriptor> EditableFields(
        ConnectionProviderDescriptor provider) => provider.GeneralFields
            .Concat(provider.AuthenticationFields)
            .Concat(provider.SecurityFields)
            .Where(field => EditableFieldKeys.Contains(field.Key))
            .ToArray();

    internal static ConnectionProviderDefaults Get(
        StorageProviderKind providerKind,
        IReadOnlyDictionary<string, string>? stored)
    {
        var provider = ConnectionProviderCatalog.Get(providerKind);
        var builtIn = BuiltInOperationalDefaults(providerKind);
        var fields = EditableFields(provider).ToDictionary(
            field => field.Key,
            field => field.DefaultValue,
            StringComparer.Ordinal);
        if (stored is not null)
        {
            foreach (var field in EditableFields(provider))
            {
                if (stored.TryGetValue(Key(providerKind, field.Key), out var value) &&
                    IsValidFieldValue(field, value))
                {
                    fields[field.Key] = value;
                }
            }
        }

        var connectTimeout = ReadBoundedInteger(
            stored,
            Key(providerKind, ConnectTimeoutKey),
            builtIn.ConnectTimeoutSeconds,
            1,
            600);
        // CL.Storage exposes one network timeout for remote providers. Keeping the
        // two profile values identical prevents creation of a profile the adapter
        // would later have to reject as unenforceable.
        var operationTimeout = providerKind == StorageProviderKind.Local
            ? ReadBoundedInteger(
                stored,
                Key(providerKind, OperationTimeoutKey),
                builtIn.OperationTimeoutSeconds,
                1,
                86_400)
            : connectTimeout;
        var retryAttempts = SupportsConfigurableRetries(providerKind)
            ? ReadBoundedInteger(
                stored,
                Key(providerKind, RetryAttemptsKey),
                builtIn.MaximumRetryAttempts,
                0,
                20)
            : 0;

        return new ConnectionProviderDefaults(
            connectTimeout,
            operationTimeout,
            retryAttempts,
            fields);
    }

    internal static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? values)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in ConnectionProviderCatalog.All)
        {
            var defaults = Get(provider.Kind, values);
            normalized[Key(provider.Kind, ConnectTimeoutKey)] = defaults.ConnectTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            normalized[Key(provider.Kind, OperationTimeoutKey)] = defaults.OperationTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            normalized[Key(provider.Kind, RetryAttemptsKey)] = defaults.MaximumRetryAttempts.ToString(CultureInfo.InvariantCulture);
            foreach (var field in EditableFields(provider))
            {
                normalized[Key(provider.Kind, field.Key)] = defaults.FieldValues[field.Key];
            }
        }

        return normalized;
    }

    internal static string Key(StorageProviderKind provider, string setting) => $"{provider}.{setting}";

    internal static bool SupportsConfigurableRetries(StorageProviderKind provider) => provider is
        StorageProviderKind.Local or StorageProviderKind.S3 or StorageProviderKind.Ssh;

    private static ConnectionProviderDefaults BuiltInOperationalDefaults(StorageProviderKind provider) => provider switch
    {
        StorageProviderKind.Local => new(30, 60, 3, new Dictionary<string, string>()),
        StorageProviderKind.S3 => new(30, 30, 3, new Dictionary<string, string>()),
        StorageProviderKind.Ftp or StorageProviderKind.Ftps or StorageProviderKind.Sftp or StorageProviderKind.Ssh =>
            new(30, 30, 0, new Dictionary<string, string>()),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static int ReadBoundedInteger(
        IReadOnlyDictionary<string, string>? values,
        string key,
        int fallback,
        int minimum,
        int maximum) => values is not null &&
        values.TryGetValue(key, out var text) &&
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) &&
        parsed >= minimum && parsed <= maximum
            ? parsed
            : fallback;

    private static bool IsValidFieldValue(ConnectionFieldDescriptor field, string value)
    {
        if (value.Length > 2_048 || value.Any(char.IsControl))
        {
            return false;
        }

        return field.Kind switch
        {
            ConnectionFieldKind.Number => int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number) && number is >= 1 and <= 65_535,
            ConnectionFieldKind.Choice => field.Choices?.Contains(value, StringComparer.Ordinal) == true,
            ConnectionFieldKind.SecretReference =>
                value.Length == 0 || ConnectionEndpointDocument.IsOpaqueSecretReference(value),
            _ => true
        };
    }
}
