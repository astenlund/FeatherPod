using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

using EpisodeModel = FeatherPod.Shared.Models.Episode;

namespace FeatherPod.Commands.Episode;

/// <summary>
/// Retries the transcription of an episode whose transcript failed. Selection offers only those episodes; an episode named by id is
/// sent to the server as given, so an ineligible one gets the server's specific refusal.
/// </summary>
internal sealed class RetryTranscriptCommand : AsyncCommand<RetryTranscriptSettings>
{
    /// <summary>
    /// Core retry operation - can be called from CLI or InteractiveCommand.
    /// </summary>
    public static async Task<EpisodeOperationResult> RetryTranscriptAsync(HttpClient httpClient, string feedId, EpisodeModel episode, CancellationToken cancellationToken = default)
    {
        var result = await EpisodeHelpers.RetryTranscriptAsync(httpClient, feedId, episode.Id, cancellationToken);
        if (result.Success)
        {
            Out.Success($"Transcript retry started for [cyan]{Markup.Escape(episode.Title)}[/] [grey](job {Markup.Escape(result.JobId ?? "unknown")})[/]");

            return new() { Success = true, EpisodeId = episode.Id };
        }

        var reason = result.Reason == null ? "" : $" [grey]({Markup.Escape(result.Reason)})[/]";
        Out.Error($"Transcript retry refused for [cyan]{Markup.Escape(episode.Title)}[/]: {Markup.Escape(result.ErrorMessage ?? "unknown error")}{reason}");

        return new() { Success = false, EpisodeId = episode.Id, ErrorMessage = result.ErrorMessage };
    }

    /// <summary>
    /// Pick one of the episodes whose transcript failed, or report that there are none. Returns null when there is nothing to pick or
    /// the pick was cancelled.
    /// </summary>
    internal static EpisodeModel? SelectFailedTranscriptEpisode(List<EpisodeModel> episodes, string feedTitle)
    {
        var eligible = EpisodeHelpers.WithFailedTranscript(episodes);
        if (eligible.Count == 0)
        {
            Out.MarkupLine($"[grey]No episode in '[cyan]{Markup.Escape(feedTitle)}[/]' has a failed transcript.[/]");

            return null;
        }

        return EpisodeHelpers.SelectEpisodeSingle(eligible);
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RetryTranscriptSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode Retry Transcript[/]");
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

        var feed = !string.IsNullOrEmpty(settings.FeedId)
            ? await FeedHelpers.GetFeedByIdAsync(httpClient, settings.FeedId)
            : await FeedHelpers.SelectFeedAsync(httpClient);

        if (feed == null)
        {
            Out.Error(!string.IsNullOrEmpty(settings.FeedId)
                ? $"Feed '{Markup.Escape(settings.FeedId)}' not found."
                : "No feeds available.");
            Out.BlankLine().Flush();

            return 1;
        }

        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, feed.Id);
        if (episodes == null || episodes.Count == 0)
        {
            Out.MarkupLine($"[yellow]Feed '[cyan]{Markup.Escape(feed.Title)}[/]' has no episodes.[/]");
            Out.BlankLine().Flush();

            return 1;
        }

        EpisodeModel? episode;
        if (!string.IsNullOrEmpty(settings.EpisodeId))
        {
            episode = episodes.FirstOrDefault(e => e.Id == settings.EpisodeId);
            if (episode == null)
            {
                Out.Error($"Episode '{Markup.Escape(settings.EpisodeId)}' not found in feed '{Markup.Escape(feed.Id)}'.");
                Out.BlankLine().Flush();

                return 1;
            }
        }
        else
        {
            episode = SelectFailedTranscriptEpisode(episodes, feed.Title);
            if (episode == null)
            {
                Out.BlankLine().Flush();

                return 1;
            }
        }

        var result = await RetryTranscriptAsync(httpClient, feed.Id, episode, cancellationToken);
        Out.BlankLine().Flush();

        return result.Success ? 0 : 1;
    }
}
