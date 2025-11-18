using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings;

internal sealed class InteractiveSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
