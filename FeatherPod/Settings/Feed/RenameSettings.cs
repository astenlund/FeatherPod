using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Feed;

internal sealed class RenameSettings : CommandSettings
{
    [CommandArgument(0, "[FEED_ID]")]
    [Description("Current feed ID")]
    public string? FeedId { get; init; }

    [CommandArgument(1, "[NEW_ID]")]
    [Description("New feed ID")]
    public string? NewId { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
