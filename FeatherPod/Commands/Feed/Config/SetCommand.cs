using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed.Config;

internal sealed class SetCommand : AsyncCommand<ConfigSetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigSetSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Feed Configuration[/]");
        Out.BlankLine();

        // Validate at least one setting is provided
        if (!settings.ExtractDate.HasValue)
        {
            Out.Error("No configuration options specified. Use -h for available options.");

            return 1;
        }

        var (httpClient, feed) = await FeedHelpers.ResolveEnvironmentAndFeedAsync(settings.Environment, settings.FeedId);
        if (httpClient == null || feed == null)
        {
            return 1;
        }

        var (success, updatedFeed, error) = await FeedHelpers.UpdateFeedConfigAsync(
            httpClient,
            feed,
            useFileMetadataForPublishDate: settings.ExtractDate,
            cancellationToken);

        if (success)
        {
            Out.Success($"Updated feed configuration: [cyan]{Markup.Escape(feed.Id)}[/]");
            Out.BlankLine();

            if (updatedFeed != null)
            {
                Out.MarkupLine($"  Extract date from file: {(updatedFeed.UseFileMetadataForPublishDate ? "[green]Yes[/]" : "[grey]No[/]")}");
            }

            Out.BlankLine().Flush();

            return 0;
        }

        Out.Error(Markup.Escape(error ?? "Unknown error"));

        return 1;
    }
}
