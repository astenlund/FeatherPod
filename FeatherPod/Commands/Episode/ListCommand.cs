using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using FeatherPod.Shared.Models;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

using EpisodeModel = FeatherPod.Shared.Models.Episode;

namespace FeatherPod.Commands.Episode;

internal sealed class ListCommand : AsyncCommand<ListSettings>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

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

            Out.MarkupLine($"Feed: [cyan]{Markup.Escape(feed.Title)}[/]");
            Out.BlankLine();

            if (episodes.Count == 0)
            {
                Out.MarkupLine("[yellow]No episodes found.[/]");
                Out.BlankLine().Flush();

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

            Out.Write(table);
            Out.BlankLine();
            Out.MarkupLine($"[grey]Total: {episodes.Count} episodes[/]");
            Out.BlankLine().Flush();

            return episodes;
        }
        catch (HttpRequestException ex)
        {
            Out.Error($"Error fetching episodes: {ex.Message}");

            return [];
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ListSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode List[/]");
        Out.BlankLine();

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
            : await FeedHelpers.SelectFeedAsync(httpClient);

        if (feed == null)
        {
            Out.Error(!string.IsNullOrEmpty(settings.FeedId)
                ? $"Feed '{settings.FeedId}' not found."
                : "No feeds available.");

            return 1;
        }

        await ListEpisodesAsync(httpClient, feed);

        return 0;
    }
}
