using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Episode;

internal sealed class PushSettings : CommandSettings
{
    [CommandArgument(0, "<files>")]
    [Description("Audio file(s) to upload (supports wildcards and comma-separated lists)")]
    public string Files { get; init; } = string.Empty;

    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }

    [CommandOption("-f|--feed")]
    [Description("Feed ID to upload to")]
    public string? FeedId { get; init; }

    [CommandOption("-t|--title")]
    [Description("Episode title (optional, defaults to filename)")]
    public string? Title { get; init; }

    [CommandOption("-d|--description")]
    [Description("Episode description (optional)")]
    public string? Description { get; init; }

    [CommandOption("-s|--summary")]
    [Description("Short summary for iTunes (optional, defaults to description)")]
    public string? Summary { get; init; }

    [CommandOption("-p|--published-date")]
    [Description("Published date in ISO 8601 format (optional)")]
    public string? PublishedDate { get; init; }

    [CommandOption("-x|--extract-date-from-file")]
    [Description("Extract published date from file metadata")]
    public bool? ExtractDateFromFile { get; init; }

    [CommandOption("-n|--normalize-on-server")]
    [Description("Normalize audio on server instead of locally")]
    public bool ServerNormalize { get; init; }

    [CommandOption("--delete-after")]
    [Description("Delete source files after successful upload")]
    public bool DeleteAfter { get; init; }

    [CommandOption("--dry-run")]
    [Description("Preview what would be uploaded and deleted without actually doing it")]
    public bool DryRun { get; init; }

    [CommandOption("--headless")]
    [Description("Run in headless mode (no console, opens browser push page)")]
    public bool Headless { get; init; }
}
