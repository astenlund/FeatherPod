using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Episode;

internal sealed class DeleteSettings : CommandSettings
{
    [CommandArgument(0, "[EPISODE_ID]")]
    [Description("Episode ID to delete")]
    public string? EpisodeId { get; init; }

    [CommandOption("-f|--feed <FEED_ID>")]
    [Description("Feed ID containing the episode")]
    public string? FeedId { get; init; }

    [CommandOption("--force")]
    [Description("Skip confirmation prompt")]
    public bool Force { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
