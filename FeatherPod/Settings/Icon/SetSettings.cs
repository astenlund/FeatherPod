using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Icon;

internal sealed class SetSettings : CommandSettings
{
    [CommandArgument(0, "<icon-path>")]
    [Description("Path to the icon file (PNG/JPEG)")]
    public string IconPath { get; init; } = string.Empty;

    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }

    [CommandOption("-f|--feed")]
    [Description("Feed ID to set icon for (optional, will prompt if omitted)")]
    public string? FeedId { get; init; }
}
