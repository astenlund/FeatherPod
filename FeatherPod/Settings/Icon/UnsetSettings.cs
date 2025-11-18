using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Icon;

internal sealed class UnsetSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }

    [CommandOption("-f|--feed")]
    [Description("Feed ID to remove icon from (optional, will prompt if omitted)")]
    public string? FeedId { get; init; }
}
