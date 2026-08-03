using System.Text.Json;

namespace StorageHub.Agent.Windows;

internal sealed record AgentConcurrencyConfiguration(
    bool Adaptive,
    int Minimum,
    int MaximumTransfers,
    int PerConnection,
    int MaximumSyncs)
{
    public static AgentConcurrencyConfiguration Defaults { get; } = new(true, 1, 4, 2, 2);

    public static AgentConcurrencyConfiguration Load(string settingsPath)
    {
        try
        {
            var file = new FileInfo(settingsPath);
            if (!file.Exists || file.Length is <= 0 or > 64 * 1024 ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Defaults;
            }

            using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var schema = ReadInt(root, "schemaVersion", 0);
            var candidate = new AgentConcurrencyConfiguration(
                ReadBool(root, "adaptiveConcurrency", Defaults.Adaptive),
                ReadInt(root, "minimumConcurrency", Defaults.Minimum),
                ReadInt(root, "maximumTransferConcurrency", Defaults.MaximumTransfers),
                ReadInt(root, "perConnectionConcurrency", Defaults.PerConnection),
                ReadInt(root, "maximumSyncConcurrency", Defaults.MaximumSyncs));
            return schema >= 4 && candidate.HasValidBounds ? candidate : Defaults;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return Defaults;
        }
    }

    private bool HasValidBounds =>
        Minimum is >= 1 and <= 8 &&
        MaximumTransfers is >= 1 and <= 32 &&
        Minimum <= MaximumTransfers &&
        PerConnection is >= 1 and <= 16 &&
        MaximumSyncs is >= 1 and <= 8 &&
        Minimum <= MaximumSyncs;

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
