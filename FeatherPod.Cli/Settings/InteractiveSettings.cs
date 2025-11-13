using Spectre.Console.Cli;
using System.ComponentModel;

namespace FeatherPod.Cli.Settings;

internal sealed class InteractiveSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
