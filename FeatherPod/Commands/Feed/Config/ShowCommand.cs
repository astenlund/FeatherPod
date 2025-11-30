using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed.Config;

internal sealed class ShowCommand : AsyncCommand<ConfigShowSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigShowSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Feed Configuration[/]");
        Out.BlankLine();

        var (httpClient, feed) = await FeedHelpers.ResolveEnvironmentAndFeedAsync(settings.Environment, settings.FeedId);
        if (httpClient == null || feed == null)
        {
            return 1;
        }

        DisplayFeedConfig(feed);
        Out.BlankLine().Flush();

        return 0;
    }

    private static void DisplayFeedConfig(Shared.Models.FeedConfig feed)
    {
        Out.MarkupLine($"Feed configuration: [cyan]{Markup.Escape(feed.Id)}[/]");
        Out.BlankLine();
        Out.MarkupLine($"  Extract date from file: {(feed.UseFileMetadataForPublishDate ? "[green]Yes[/]" : "[grey]No[/]")}");
    }
}
