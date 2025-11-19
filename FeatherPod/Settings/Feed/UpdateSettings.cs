using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Feed;

internal sealed class UpdateSettings : CommandSettings
{
    [CommandArgument(0, "[FEED_ID]")]
    [Description("Feed ID to update")]
    public string? FeedId { get; init; }

    [CommandOption("-t|--title <TITLE>")]
    [Description("New feed title")]
    public string? Title { get; init; }

    [CommandOption("-a|--author <AUTHOR>")]
    [Description("New feed author")]
    public string? Author { get; init; }

    [CommandOption("-d|--description <DESC>")]
    [Description("New feed description")]
    public string? Description { get; init; }

    [CommandOption("--email <EMAIL>")]
    [Description("New author email")]
    public string? Email { get; init; }

    [CommandOption("-l|--language <LANG>")]
    [Description("New feed language")]
    public string? Language { get; init; }

    [CommandOption("-c|--category <CAT>")]
    [Description("New podcast category")]
    public string? Category { get; init; }

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
