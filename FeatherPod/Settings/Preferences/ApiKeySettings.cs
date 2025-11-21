using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Preferences;

internal sealed class ApiKeyShowSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}

internal sealed class ApiKeySetSettings : CommandSettings
{
    [CommandArgument(0, "<key>")]
    [Description("The API key to set")]
    public string Key { get; init; } = string.Empty;

    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
