using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Feed;

internal sealed class ListSettings : CommandSettings
{
    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
