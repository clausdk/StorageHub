using System.Text.Json;

namespace StorageHub.Agent.Windows.Tests;

public sealed class AgentConcurrencyConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"storagehub-concurrency-config-{Guid.NewGuid():N}");

    [Fact]
    public void Loads_bounded_desktop_policy_for_every_agent_worker_type()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 4,
            adaptiveConcurrency = true,
            minimumConcurrency = 2,
            maximumTransferConcurrency = 12,
            perConnectionConcurrency = 3,
            maximumSyncConcurrency = 5
        }));

        var result = AgentConcurrencyConfiguration.Load(path);

        Assert.True(result.Adaptive);
        Assert.Equal(2, result.Minimum);
        Assert.Equal(12, result.MaximumTransfers);
        Assert.Equal(3, result.PerConnection);
        Assert.Equal(5, result.MaximumSyncs);
    }

    [Fact]
    public void Rejects_out_of_bounds_policy_as_a_complete_unit()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """
            {"schemaVersion":4,"adaptiveConcurrency":true,"minimumConcurrency":1,
             "maximumTransferConcurrency":99,"perConnectionConcurrency":2,"maximumSyncConcurrency":2}
            """);

        Assert.Equal(AgentConcurrencyConfiguration.Defaults, AgentConcurrencyConfiguration.Load(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
