using System.ComponentModel.DataAnnotations;
using CodeLogic.Core.Configuration;

namespace StorageHub.Application;

/// <summary>
/// Non-secret application settings managed by CodeLogic. Connection profiles,
/// credential material, trust records, and provider tokens are deliberately not
/// represented by this model.
/// </summary>
public sealed class StorageHubApplicationConfig : ConfigModelBase
{
    public string Theme { get; set; } = "System";

    public bool MinimizeToTray { get; set; } = true;

    public bool StartAgentAtSignIn { get; set; } = true;

    [Range(1, 32)]
    public int GlobalTransferConcurrency { get; set; } = 4;

    [Range(1, 16)]
    public int PerConnectionTransferConcurrency { get; set; } = 2;

    [Range(1, 365)]
    public int LogRetentionDays { get; set; } = 14;

    [Range(1, 4096)]
    public int LogSizeLimitMiB { get; set; } = 50;

    [Range(128, 32768)]
    public int CacheSizeLimitMiB { get; set; } = 2048;
}
