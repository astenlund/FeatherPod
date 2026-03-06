using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Preferences;

internal sealed class KeyShowSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}

internal sealed class KeySetSettings : CommandSettings
{
    [CommandArgument(0, "[key]")]
    [Description("The API key to set (will prompt if not provided)")]
    public string? Key { get; init; }

    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
