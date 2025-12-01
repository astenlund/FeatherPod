using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

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
        Out.BlankLine().Flush();

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
                Out.Cancelled();

                return new() { Success = false };
            }
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/feeds/{feedId}/episodes/{episode.Id}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                Out.Success($"Deleted: {Markup.Escape(episode.Title)}");

                return new() { Success = true, EpisodeId = episode.Id };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Out.MarkupLine("[yellow]Episode not found (may have already been deleted).[/]");

                return new() { Success = false, ErrorMessage = "Episode not found" };
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Out.Error($"Failed to delete episode: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                Out.Error(Markup.Escape(errorContent));
            }

            return new() { Success = false, ErrorMessage = errorContent };
        }
        catch (Exception ex)
        {
            Out.Error($"Error deleting episode: {ex.Message}");

            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, DeleteSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode Delete[/]");
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
            : await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);

        if (feed == null)
        {
            Out.Error(!string.IsNullOrEmpty(settings.FeedId)
                ? $"Feed '{settings.FeedId}' not found."
                : "No feeds available.");
            Out.BlankLine().Flush();

            return 1;
        }

        // Get episodes
        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, feed.Id);
        if (episodes == null || episodes.Count == 0)
        {
            Out.MarkupLine($"[yellow]Feed '[cyan]{Markup.Escape(feed.Title)}[/]' has no episodes.[/]");
            Out.BlankLine().Flush();

            return 1;
        }

        // Find episode(s) by ID or select interactively
        List<EpisodeModel> episodesToDelete;

        if (!string.IsNullOrEmpty(settings.EpisodeId))
        {
            var episode = episodes.FirstOrDefault(e => e.Id == settings.EpisodeId);
            if (episode == null)
            {
                Out.Error($"Episode '{settings.EpisodeId}' not found in feed '{feed.Id}'.");
                Out.BlankLine().Flush();

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
                Out.Cancelled();
                Out.BlankLine().Flush();

                return 1;
            }
        }

        // Single episode - use standard confirmation
        if (episodesToDelete.Count == 1)
        {
            var result = await DeleteEpisodeAsync(httpClient, feed.Id, episodesToDelete[0], settings.Force, cancellationToken);

            if (result.Success)
            {
                Out.BlankLine().Flush();
            }

            return result.Success ? 0 : 1;
        }

        // Multiple episodes - confirm once for all
        if (!settings.Force)
        {
            Out.MarkupLine($"[yellow]About to delete {episodesToDelete.Count} episodes:[/]");
            foreach (var ep in episodesToDelete)
            {
                Out.MarkupLine($"  • {Markup.Escape(ep.Title)}");
            }
            Out.BlankLine();

            var confirmed = new MenuBuilder<bool?>()
                .WithTitle($"[red]Delete all {episodesToDelete.Count} episodes?[/]")
                .WithHint("(arrow keys or Y/N, Esc to cancel)")
                .AddOption("Y", "Yes", true)
                .AddOption("N", "No", false)
                .AllowCancel(true, false)
                .Show();

            if (confirmed != true)
            {
                Out.Cancelled();

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

        Out.BlankLine();
        Out.Success($"Deleted {successCount}/{episodesToDelete.Count} episodes.");
        Out.BlankLine().Flush();

        return successCount == episodesToDelete.Count ? 0 : 1;
    }
}
