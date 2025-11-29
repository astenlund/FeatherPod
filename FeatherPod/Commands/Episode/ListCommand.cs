using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using FeatherPod.Shared.Models;
using Spectre.Console;
using Spectre.Console.Cli;

using EpisodeModel = FeatherPod.Shared.Models.Episode;

namespace FeatherPod.Commands.Episode;

internal sealed class ListCommand : AsyncCommand<ListSettings>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Core list operation - fetches and displays episodes. Returns the episode list.
    /// </summary>
    public static async Task<List<EpisodeModel>> ListEpisodesAsync(HttpClient httpClient, FeedConfig feed)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/feeds/{feed.Id}/episodes");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var episodes = JsonSerializer.Deserialize<List<EpisodeModel>>(json, JsonOptions) ?? [];

            AnsiConsole.MarkupLine($"Feed: [cyan]{Markup.Escape(feed.Title)}[/]");
            AnsiConsole.WriteLine();

            if (episodes.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No episodes found.[/]");
                AnsiConsole.WriteLine();

                return episodes;
            }

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn("#");
            table.AddColumn("Published");
            table.AddColumn("Title");
            table.AddColumn("URL");
            table.AddColumn("Size");
            table.AddColumn("Duration");

            for (var i = 0; i < episodes.Count; i++)
            {
                var episode = episodes[i];
                var formattedDate = episode.PublishedDate.ToString("yyyy-MM-dd HH:mm");
                var formattedSize = EpisodeHelpers.FormatFileSize(episode.FileSize);
                var formattedDuration = EpisodeHelpers.FormatDuration(episode.Duration);

                table.AddRow(
                    $"[grey]{i + 1}[/]",
                    $"[grey]{formattedDate}[/]",
                    Markup.Escape(episode.Title),
                    $"[cyan]{Markup.Escape(episode.Url ?? string.Empty)}[/]",
                    formattedSize,
                    formattedDuration
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[grey]Total: {episodes.Count} episodes[/]");
            AnsiConsole.WriteLine();

            return episodes;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching episodes:[/] {ex.Message}");

            return [];
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ListSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode List[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null)
        {
            return 1;
        }

        // Select feed
        var feed = !string.IsNullOrEmpty(settings.FeedId)
            ? await FeedHelpers.GetFeedByIdAsync(httpClient, settings.FeedId)
            : await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);

        if (feed == null)
        {
            AnsiConsole.MarkupLine(!string.IsNullOrEmpty(settings.FeedId)
                ? $"[red]Error:[/] Feed '{settings.FeedId}' not found."
                : "[red]Error:[/] No feeds available.");

            return 1;
        }

        await ListEpisodesAsync(httpClient, feed);

        return 0;
    }
}
