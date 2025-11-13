using Spectre.Console.Cli;
using System.ComponentModel;

namespace FeatherPod.Cli.Settings;

internal sealed class PushSettings : CommandSettings
{
    [CommandArgument(0, "<files>")]
    [Description("Audio file(s) to upload (supports wildcards and comma-separated lists)")]
    public string Files { get; init; } = string.Empty;

    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }

    [CommandOption("-t|--title")]
    [Description("Episode title (optional, defaults to filename)")]
    public string? Title { get; init; }

    [CommandOption("-d|--description")]
    [Description("Episode description (optional)")]
    public string? Description { get; init; }

    [CommandOption("-p|--published-date")]
    [Description("Published date in ISO 8601 format (optional)")]
    public string? PublishedDate { get; init; }

    [CommandOption("-x|--extract-date-from-file")]
    [Description("Extract published date from file metadata")]
    public bool ExtractDateFromFile { get; init; }
}
