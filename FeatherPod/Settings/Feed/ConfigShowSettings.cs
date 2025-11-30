using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Feed;

internal sealed class ConfigShowSettings : CommandSettings
{
    [CommandOption("-f|--feed <FEED>")]
    [Description("Feed ID")]
    public string? FeedId { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
