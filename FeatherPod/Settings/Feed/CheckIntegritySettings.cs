using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Feed;

internal sealed class CheckIntegritySettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }

    [CommandOption("-f|--feed-id")]
    [Description("Feed ID to check (checks all accessible feeds if not specified)")]
    public string? FeedId { get; init; }
}
