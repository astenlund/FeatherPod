using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Episode;

internal sealed class ListSettings : CommandSettings
{
    [CommandOption("-f|--feed <FEED_ID>")]
    [Description("Feed ID to list episodes from")]
    public string? FeedId { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
