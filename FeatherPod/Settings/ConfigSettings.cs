using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings;

internal sealed class ConfigSetSettings : CommandSettings
{
    [CommandArgument(0, "<KEY>")]
    [Description("Configuration key to set (e.g., 'api-key')")]
    public string Key { get; init; } = string.Empty;

    [CommandArgument(1, "<VALUE>")]
    [Description("Value to set")]
    public string Value { get; init; } = string.Empty;

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}

internal sealed class ConfigShowSettings : CommandSettings
{
    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
