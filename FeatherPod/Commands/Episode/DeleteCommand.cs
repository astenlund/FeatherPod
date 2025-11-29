using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;
using EpisodeModel = FeatherPod.Shared.Models.Episode;

namespace FeatherPod.Commands.Episode;

internal sealed class DeleteCommand : AsyncCommand<DeleteSettings>
{
    /// <summary>
    /// Core delete operation - can be called from CLI or InteractiveCommand.
    /// </summary>
    public static async Task<EpisodeOperationResult> DeleteEpisodeAsync(
        HttpClient httpClient,
        string feedId,
        EpisodeModel episode,
        bool skipConfirmation = false,
        CancellationToken cancellationToken = default)
    {
        // Confirm deletion unless skipped
        if (!skipConfirmation)
        {
            var confirmed = new MenuBuilder<bool?>()
                .WithTitle($"[red]Delete[/] {Markup.Escape(episode.Title)}?")
                .WithHint("(arrow keys or Y/N, Esc to cancel)")
                .AddOption("Y", "Yes", true)
                .AddOption("N", "No", false)
                .AllowCancel(true, false)
                .Show();

            if (confirmed != true)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");

                return new() { Success = false };
            }
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/feeds/{feedId}/episodes/{episode.Id}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Deleted: {Markup.Escape(episode.Title)}");

                return new() { Success = true, EpisodeId = episode.Id };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                AnsiConsole.MarkupLine("[yellow]Episode not found (may have already been deleted).[/]");

                return new() { Success = false, ErrorMessage = "Episode not found" };
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            AnsiConsole.MarkupLine($"[red]✗[/] Failed to delete episode: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
            }

            return new() { Success = false, ErrorMessage = errorContent };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error deleting episode: {ex.Message}");

            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

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
            AnsiConsole.WriteLine();

            return 1;
        }

        // Get episodes
        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, feed.Id);
        if (episodes == null || episodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Feed '[cyan]{Markup.Escape(feed.Title)}[/]' has no episodes.[/]");
            AnsiConsole.WriteLine();

            return 1;
        }

        // Find episode(s) by ID or select interactively
        List<EpisodeModel> episodesToDelete;

        if (!string.IsNullOrEmpty(settings.EpisodeId))
        {
            var episode = episodes.FirstOrDefault(e => e.Id == settings.EpisodeId);
            if (episode == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Episode '{settings.EpisodeId}' not found in feed '{feed.Id}'.");
                AnsiConsole.WriteLine();

                return 1;
            }

            episodesToDelete = [episode];
        }
        else
        {
            // Interactive selection
            episodesToDelete = EpisodeHelpers.SelectEpisodesMulti(episodes);
            if (episodesToDelete.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                AnsiConsole.WriteLine();

                return 1;
            }
        }

        // Single episode - use standard confirmation
        if (episodesToDelete.Count == 1)
        {
            var result = await DeleteEpisodeAsync(httpClient, feed.Id, episodesToDelete[0], settings.Force, cancellationToken);

            if (result.Success)
            {
                AnsiConsole.WriteLine();
            }

            return result.Success ? 0 : 1;
        }

        // Multiple episodes - confirm once for all
        if (!settings.Force)
        {
            AnsiConsole.MarkupLine($"[yellow]About to delete {episodesToDelete.Count} episodes:[/]");
            foreach (var ep in episodesToDelete)
            {
                AnsiConsole.MarkupLine($"  • {Markup.Escape(ep.Title)}");
            }
            AnsiConsole.WriteLine();

            var confirmed = new MenuBuilder<bool?>()
                .WithTitle($"[red]Delete all {episodesToDelete.Count} episodes?[/]")
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

        var successCount = 0;
        foreach (var episode in episodesToDelete)
        {
            var result = await DeleteEpisodeAsync(httpClient, feed.Id, episode, skipConfirmation: true, cancellationToken);
            if (result.Success)
            {
                successCount++;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Deleted {successCount}/{episodesToDelete.Count} episodes.[/]");
        AnsiConsole.WriteLine();

        return successCount == episodesToDelete.Count ? 0 : 1;
    }
}
