using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Episode;

internal sealed class DeleteCommand : AsyncCommand<DeleteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeleteSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Delete[/]");
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

        // Get episodes
        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, feed.Id);
        if (episodes == null || episodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Feed '[cyan]{Markup.Escape(feed.Title)}[/]' has no episodes.[/]");

            return 1;
        }

        // Find episode by ID or select interactively
        Server.Models.Episode? episodeToDelete;

        if (!string.IsNullOrEmpty(settings.EpisodeId))
        {
            episodeToDelete = episodes.FirstOrDefault(e => e.Id == settings.EpisodeId);
            if (episodeToDelete == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Episode '{settings.EpisodeId}' not found in feed '{feed.Id}'.");

                return 1;
            }
        }
        else
        {
            // Interactive selection
            var selected = EpisodeHelpers.SelectEpisodesMulti(episodes);
            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");

                return 1;
            }
            if (selected.Count > 1)
            {
                AnsiConsole.MarkupLine("[yellow]Multiple episodes selected. Use episode move/copy for batch operations, or delete one at a time.[/]");

                return 1;
            }

            episodeToDelete = selected[0];
        }

        // Confirm deletion unless --force
        if (!settings.Force)
        {
            var confirmed = new MenuBuilder<bool?>()
                .WithTitle($"[red]Delete[/] {Markup.Escape(episodeToDelete.Title)}?")
                .WithHint("(arrow keys or Y/N, Esc to cancel)")
                .AddOption("Y", "Yes", true)
                .AddOption("N", "No", false)
                .AllowCancel(true, false)
                .Show();

            if (confirmed != true)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");

                return 1;
            }
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/feeds/{feed.Id}/episodes/{episodeToDelete.Id}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Deleted: {Markup.Escape(episodeToDelete.Title)}");
                AnsiConsole.WriteLine();

                return 0;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine("[yellow]Episode not found (may have already been deleted).[/]");

                return 1;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            AnsiConsole.MarkupLine($"[red]✗[/] Failed to delete episode: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
            }

            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error deleting episode: {ex.Message}");
            return 1;
        }
    }
}
