using Spectre.Console.Cli;
using System.ComponentModel;

namespace FeatherPod.Cli.Settings;

internal sealed class IconUnsetSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }

    [CommandOption("-f|--feed")]
    [Description("Feed ID to remove icon from (optional, will prompt if omitted)")]
    public string? FeedId { get; init; }
}
