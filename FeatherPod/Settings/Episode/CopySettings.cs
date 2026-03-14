using Spectre.Console.Cli;
using System.ComponentModel;

namespace FeatherPod.Settings.Episode;

internal sealed class CopySettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }

    [CommandOption("-f|--from-feed")]
    [Description("Source feed ID")]
    public string? FromFeed { get; init; }

    [CommandOption("-t|--to-feed")]
    [Description("Target feed ID")]
    public string? ToFeed { get; init; }

    [CommandOption("--episode")]
    [Description("Episode pattern to match (exact ID, wildcard by filename/title, or '*' for all)")]
    public string? Episode { get; init; }
}
