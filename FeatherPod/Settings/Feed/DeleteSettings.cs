using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Feed;

internal sealed class DeleteSettings : CommandSettings
{
    [CommandArgument(0, "[FEED_ID]")]
    [Description("Feed ID to delete")]
    public string? FeedId { get; init; }

    [CommandOption("--force")]
    [Description("Skip confirmation prompt")]
    public bool Force { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
