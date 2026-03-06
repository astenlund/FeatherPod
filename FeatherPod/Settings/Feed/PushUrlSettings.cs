using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Feed;

internal sealed class PushUrlSettings : CommandSettings
{
    [CommandOption("-f|--feed <FEED_ID>")]
    [Description("Feed ID")]
    public string? FeedId { get; init; }

    [CommandOption("-c|--copy")]
    [Description("Copy URL to clipboard")]
    public bool Copy { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
