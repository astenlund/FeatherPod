using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings;

internal sealed class VersionSettings : CommandSettings
{
    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
