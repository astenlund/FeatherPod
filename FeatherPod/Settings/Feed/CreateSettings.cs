using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Feed;

internal sealed class CreateSettings : CommandSettings
{
    [CommandOption("--id <ID>")]
    [Description("Feed ID (URL-friendly slug)")]
    public string? Id { get; init; }

    [CommandOption("-t|--title <TITLE>")]
    [Description("Feed title")]
    public string? Title { get; init; }

    [CommandOption("-a|--author <AUTHOR>")]
    [Description("Feed author")]
    public string? Author { get; init; }

    [CommandOption("-d|--description <DESC>")]
    [Description("Feed description")]
    public string? Description { get; init; }

    [CommandOption("-s|--summary <SUMMARY>")]
    [Description("Short summary for iTunes (defaults to description)")]
    public string? Summary { get; init; }

    [CommandOption("-m|--email <EMAIL>")]
    [Description("Author email")]
    public string? Email { get; init; }

    [CommandOption("-l|--language <LANG>")]
    [Description("Feed language (default: en)")]
    public string? Language { get; init; }

    [CommandOption("-c|--category <CAT>")]
    [Description("Podcast category")]
    public string? Category { get; init; }

    [CommandOption("-i|--icon <PATH>")]
    [Description("Icon file path (PNG/JPEG)")]
    public string? IconPath { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
