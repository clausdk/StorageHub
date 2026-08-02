using CL.Storage;
using CL.Storage.Configuration;
using CodeLogic;
using StorageHub.Agent;
using StorageHub.Agent.Ipc;
using StorageHub.Agent.Scheduling;
using StorageHub.Agent.Sync;
using StorageHub.Agent.Transfers;
using StorageHub.Agent.Windows;
using StorageHub.Application;
using StorageHub.Contracts.Ipc;
using StorageHub.Infrastructure.Windows;
using StorageHub.Persistence;
using StorageHub.Persistence.Connections;
using StorageHub.Persistence.Scheduling;
using StorageHub.Persistence.Sync;
using StorageHub.Persistence.Transfers;
using StorageHub.Persistence.Trust;
using StorageHub.Storage.CodeLogic;
using StorageHub.Sync;

var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var configuredStorageHubRoot = Environment.GetEnvironmentVariable("STORAGEHUB_DATA_ROOT");
if (string.IsNullOrWhiteSpace(configuredStorageHubRoot))
{
    configuredStorageHubRoot = Path.Combine(localAppData, "StorageHub");
}

WindowsAgentDataDirectoryLease agentDataDirectoryLease;
try
{
    agentDataDirectoryLease = WindowsAgentDataDirectoryLease.Acquire(
        configuredStorageHubRoot);
}
catch (WindowsAgentDataDirectoryException error)
{
    Console.Error.WriteLine($"StorageHub Agent data directory rejected: {error.Message}");
    return 1;
}
catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
{
    Console.Error.WriteLine("StorageHub Agent data directory rejected: the configured path is invalid.");
    return 1;
}

using var agentDataDirectoryLifetime = agentDataDirectoryLease;
var storageHubRoot = agentDataDirectoryLease.RootDirectory;
var agentRoot = agentDataDirectoryLease.AgentDirectory;
var runtimeSecretFileMaterializer = new WindowsRuntimeSecretFileMaterializer(
    Path.Combine(agentRoot, "runtime-secrets"));
_ = runtimeSecretFileMaterializer.ScavengeOrphans(TimeSpan.FromHours(24));

var initialization = await CodeLogic.CodeLogic.InitializeAsync(options =>
{
    options.FrameworkRootPath = agentDataDirectoryLease.FrameworkDirectory;
    options.ApplicationRootPath = agentRoot;
    options.AppVersion = "0.1.0";
    options.HandleShutdownSignals = false;
});

if (!initialization.Success || initialization.ShouldExit)
{
    Console.Error.WriteLine($"StorageHub Agent startup failed: {initialization.Message}");
    return 1;
}

await Libraries.LoadAsync<StorageLibrary>();
// Until CL.Storage ships RuntimeOnly mode, disabling configured connections is
// the only safe bootstrap: runtime backends are registered by StorageHub and no
// provider credential is ever written through CodeLogic configuration.
Libraries.OverrideConfig<StorageConfig>("CL.Storage", "storage", config => config.Enabled = false);

var agentInstanceId = Guid.NewGuid();
AgentRuntimeCoordinator? coordinator = null;
var databaseOptions = new SqliteDatabaseOptions(
    Path.Combine(agentRoot, "storagehub.db"));
var databaseSubsystem = new DatabaseAgentSubsystem(databaseOptions);
using var vaultSubsystem = new SecretVaultAgentSubsystem(
    Path.Combine(agentRoot, "vault"));
var schedulerDatabase = new SingleWriterSqliteDatabase(databaseOptions);
var schedulerStore = new SqliteScheduledSyncJobStore(schedulerDatabase);
var scheduleManagementRepository = new SqliteSyncScheduleManagementRepository(schedulerDatabase);
var schedulerDispatchStore = new SqliteScheduledSyncDispatchStore(schedulerDatabase);
await using var schedulerSubsystem = new SchedulerAgentSubsystem(
    schedulerStore,
    new DurableScheduledSyncJobRunner(schedulerDispatchStore));
var transferDatabase = new SingleWriterSqliteDatabase(databaseOptions);
var transferStore = new SqliteTransferJobStore(transferDatabase);
var transferTrustStore = new SqliteTrustStore(transferDatabase);
var transferProfiles = new SqliteConnectionProfileRepository(databaseOptions);
var transferEndpointConnector = new CodeLogicTransferEndpointConnector(
    transferProfiles,
    () => new CodeLogicConnectionProfileConnector(
        new CodeLogicStorageSessionFactory(
            Libraries.Get<StorageLibrary>() ??
            throw new InvalidOperationException("CL.Storage is not configured.")),
        vaultSubsystem.Vault,
        transferTrustStore,
        runtimeSecretFileMaterializer));
await using var transferQueueSubsystem = new TransferQueueAgentSubsystem(
    transferStore,
    transferEndpointConnector);
var storageCommands = new StorageIpcCommandService(
    databaseOptions,
    () => vaultSubsystem.Vault,
    runtimeSecretFileMaterializer,
    () => new CodeLogicStorageSessionFactory(
        Libraries.Get<StorageLibrary>() ??
        throw new InvalidOperationException("CL.Storage is not configured.")));
var profileCommands = new ConnectionProfileIpcCommandService(databaseOptions);
var transferCommands = new TransferQueueIpcCommandService(
    transferStore,
    transferStore,
    transferQueueSubsystem);
var syncProfiles = new SqliteSyncProfileRepository(transferDatabase);
var syncBaselines = new SqliteSyncBaselineStore(transferDatabase);
var syncPlans = new SqliteSyncPlanStore(transferDatabase);
var syncRuns = new SqliteSyncRunStore(transferDatabase);
var syncConflicts = new SqliteSyncConflictStore(transferDatabase);
var syncConnector = new CodeLogicSyncEndpointConnector(
    transferProfiles,
    () => new CodeLogicConnectionProfileConnector(
        new CodeLogicStorageSessionFactory(
            Libraries.Get<StorageLibrary>() ??
            throw new InvalidOperationException("CL.Storage is not configured.")),
        vaultSubsystem.Vault,
        transferTrustStore,
        runtimeSecretFileMaterializer));
var syncOrchestration = new SyncOrchestrationService(
    syncProfiles,
    syncBaselines,
    syncPlans,
    syncRuns,
    syncConflicts,
    syncConnector);
var syncOutbox = new SqliteReliableOutboxStore(transferDatabase);
var syncExecution = new SqliteSyncExecutionStore(transferDatabase);
var syncOutboxProcessor = new SyncOutboxEventProcessor(
    syncOrchestration,
    syncProfiles,
    syncPlans,
    syncExecution,
    syncConnector);
await using var syncOutboxSubsystem = new SyncOutboxAgentSubsystem(
    syncOutbox,
    syncOutboxProcessor);
var syncCommands = new SyncManagementIpcCommandService(
    syncProfiles,
    syncOrchestration,
    syncRuns,
    syncPlans,
    syncConflicts);
var scheduleCommands = new ScheduleManagementIpcCommandService(scheduleManagementRepository);
var objectInspectorCommands = new ObjectInspectorIpcCommandService(syncConnector);
var requestHandler = new AgentIpcRequestHandler(
    () => CreateStatusSnapshot(
        coordinator,
        agentInstanceId,
        transferQueueSubsystem.ActiveExecutionCount,
        syncOutboxSubsystem.ActiveCount),
    new CompositeAgentIpcCommandHandler(
        storageCommands,
        profileCommands,
        transferCommands,
        syncCommands,
        scheduleCommands,
        objectInspectorCommands));
var ipc = new NamedPipeIpcServerSubsystem(
    new NamedPipeIpcServerOptions
    {
        PipeName = "StorageHub.Agent.v1",
        AgentVersion = "0.1.0",
        AgentInstanceId = agentInstanceId,
        MaxConcurrentClients = 8,
        RequestTimeout = TimeSpan.FromMinutes(2),
        SessionIdleTimeout = TimeSpan.FromMinutes(3)
    },
    requestHandler.HandleSessionAsync);
var secretRequestHandler = new AgentSecretIpcRequestHandler(
    new SecretVaultIpcCommandService(() => vaultSubsystem.Vault));
var secretIpc = new NamedPipeIpcServerSubsystem(
    new NamedPipeIpcServerOptions
    {
        PipeName = "StorageHub.Agent.Secrets.v1",
        AgentVersion = "0.1.0",
        AgentInstanceId = agentInstanceId,
        MaxConcurrentClients = 2,
        RequestTimeout = TimeSpan.FromSeconds(30),
        FrameKind = IpcFrameKind.Secret
    },
    secretRequestHandler.HandleSessionAsync);
await using var runtimeCoordinator = new AgentRuntimeCoordinator(
    [
        databaseSubsystem,
        vaultSubsystem,
        transferQueueSubsystem,
        syncOutboxSubsystem,
        schedulerSubsystem,
        ipc,
        secretIpc
    ]);
coordinator = runtimeCoordinator;
CodeLogic.CodeLogic.RegisterApplication(new StorageHubApplication(runtimeCoordinator));

try
{
    await CodeLogic.CodeLogic.ConfigureAsync();
    await CodeLogic.CodeLogic.StartAsync();

    if (initialization.RunHealthCheck || args.Contains("--health", StringComparer.OrdinalIgnoreCase))
    {
        var report = await CodeLogic.CodeLogic.GetHealthAsync();
        Console.WriteLine(report.ToConsoleString());
        return report.IsHealthy ? 0 : 2;
    }

    if (args.Contains("--run-once", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("StorageHub Agent initialized successfully.");
        return 0;
    }

    Console.WriteLine("StorageHub Agent is running. Press Ctrl+C to stop.");
    var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.TrySetResult();
    };

    await shutdown.Task.ConfigureAwait(false);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"StorageHub Agent failed: {error.GetType().Name}");
    return 1;
}
finally
{
    await CodeLogic.CodeLogic.StopAsync();
}

static AgentStatusSnapshot CreateStatusSnapshot(
    AgentRuntimeCoordinator? coordinator,
    Guid agentInstanceId,
    int activeTransfers,
    int activeSyncRuns)
{
    var state = coordinator?.State switch
    {
        ApplicationOperationalState.Ready => AgentLifecycleState.Ready,
        ApplicationOperationalState.RecoveryOnly => AgentLifecycleState.Degraded,
        ApplicationOperationalState.Faulted => AgentLifecycleState.Faulted,
        ApplicationOperationalState.Stopping or ApplicationOperationalState.Stopped => AgentLifecycleState.Stopping,
        _ => AgentLifecycleState.Starting
    };

    return new AgentStatusSnapshot(
        agentInstanceId,
        state,
        DateTimeOffset.UtcNow,
        ActiveTransfers: activeTransfers,
        ActiveSyncRuns: activeSyncRuns,
        coordinator?.HealthMessage);
}
