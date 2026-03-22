using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Episode;

internal sealed class RenameSettings : CommandSettings
{
    [CommandArgument(0, "[EPISODE_ID]")]
    [Description("Episode ID to rename")]
    public string? EpisodeId { get; init; }

    [CommandOption("-f|--feed <FEED_ID>")]
    [Description("Feed ID containing the episode")]
    public string? FeedId { get; init; }

    [CommandOption("-t|--title <TITLE>")]
    [Description("New title for the episode")]
    public string? NewTitle { get; init; }

    [CommandOption("--suggest")]
    [Description("Fetch an AI-suggested title")]
    public bool Suggest { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
