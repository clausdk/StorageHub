using System.Reflection;

namespace StorageHub.Desktop;

public static class DesktopApplicationVersion
{
    private const string UnknownVersion = "0.0.0";

    public static string Current => Resolve(Assembly.GetEntryAssembly() ?? typeof(DesktopApplicationVersion).Assembly);

    public static string Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Trim();
        if (IsUsable(informational))
        {
            var metadataSeparator = informational!.IndexOf('+', StringComparison.Ordinal);
            return metadataSeparator < 0
                ? informational
                : informational[..metadataSeparator];
        }

        var version = assembly.GetName().Version?.ToString();
        return IsUsable(version) ? version! : UnknownVersion;
    }

    private static bool IsUsable(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        !value.Any(char.IsControl);
}
