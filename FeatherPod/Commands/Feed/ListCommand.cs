using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed;

internal sealed class ListCommand : AsyncCommand<ListSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ListSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Feed Management - List Feeds[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        var feeds = await FeedHelpers.GetFeedsAsync(httpClient);

        if (feeds.Count == 0)
        {
            Out.MarkupLine("[yellow]No feeds found.[/]");
            return 0;
        }

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("[bold]Feed ID[/]");
        table.AddColumn("[bold]Title[/]");
        table.AddColumn("[bold]Author[/]");
        table.AddColumn("[bold]Language[/]");
        table.AddColumn("[bold]Category[/]");

        foreach (var feed in feeds)
        {
            table.AddRow(
                Markup.Escape(feed.Id),
                Markup.Escape(feed.Title),
                Markup.Escape(feed.Author),
                Markup.Escape(feed.Language),
                Markup.Escape(feed.Category ?? "-")
            );
        }

        Out.Write(table);
        Out.BlankLine();
        Out.MarkupLine($"[grey]Total: {feeds.Count} feed(s)[/]");
        Out.BlankLine().Flush();

        return 0;
    }
}
