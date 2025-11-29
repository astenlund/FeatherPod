using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

using EpisodeModel = FeatherPod.Shared.Models.Episode;
using FeedConfig = FeatherPod.Shared.Models.FeedConfig;

namespace FeatherPod.Commands.Episode;

internal sealed class MoveCommand : AsyncCommand<MoveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, MoveSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode Move[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Determine source feed
        FeedConfig? sourceFeed;
        if (!string.IsNullOrEmpty(settings.FromFeed))
        {
            sourceFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, settings.FromFeed);
            if (sourceFeed == null)
            {
                Out.Error($"Source feed '{settings.FromFeed}' not found.");
                return 1;
            }
        }
        else
        {
            sourceFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, contextMessage: "Select source feed:");
            if (sourceFeed == null)
            {
                Out.Error("No feeds available.");
                return 1;
            }
        }

        // Get episodes from source feed
        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, sourceFeed.Id);
        if (episodes == null || episodes.Count == 0)
        {
            Out.MarkupLine($"[yellow]Feed '[cyan]{Markup.Escape(sourceFeed.Title)}[/]' has no episodes.[/]");
            return 1;
        }

        // Determine which episodes to move
        List<EpisodeModel> episodesToMove;
        if (!string.IsNullOrEmpty(settings.Episode))
        {
            // CLI mode: use pattern matching
            episodesToMove = EpisodeHelpers.MatchEpisodesByPattern(episodes, settings.Episode);
            if (episodesToMove.Count == 0)
            {
                Out.Error($"No episodes match pattern '{settings.Episode}'");
                return 1;
            }

            Out.MarkupLine($"Matched [bold]{episodesToMove.Count}[/] episode(s) from feed '[cyan]{Markup.Escape(sourceFeed.Title)}[/]'");
            Out.BlankLine();
        }
        else
        {
            // Interactive mode: multi-select
            episodesToMove = EpisodeHelpers.SelectEpisodesMulti(episodes);
            if (episodesToMove.Count == 0)
            {
                Out.Cancelled();
                return 1;
            }
            Out.BlankLine();
        }

        // Determine target feed
        FeedConfig? targetFeed;
        if (!string.IsNullOrEmpty(settings.ToFeed))
        {
            targetFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, settings.ToFeed);
            if (targetFeed == null)
            {
                Out.Error($"Target feed '{settings.ToFeed}' not found.");
                return 1;
            }
        }
        else
        {
            // Get all feeds except the source for selection
            var allFeeds = await FeedHelpers.GetFeedsAsync(httpClient);
            if (allFeeds.Count <= 1)
            {
                Out.Error("No other feeds available. Create another feed first.");
                return 1;
            }

            var targetFeeds = allFeeds.Where(f => f.Id != sourceFeed.Id).ToList();
            if (targetFeeds.Count == 0)
            {
                Out.Error("No other feeds available.");
                return 1;
            }

            var menu = new MenuBuilder<FeedConfig?>()
                .WithTitle("Select target feed:")
                .AllowCancel();

            foreach (var feed in targetFeeds)
            {
                menu.AddOption(null, Markup.Escape(feed.Title), feed);
            }

            targetFeed = menu.Show();
            if (targetFeed == null)
            {
                Out.Cancelled();
                return 1;
            }
            Out.BlankLine();
        }

        // Validate: source != target
        if (sourceFeed.Id == targetFeed.Id)
        {
            Out.Error("Cannot move episodes within the same feed.");
            return 1;
        }

        // Confirmation
        var episodeWord = episodesToMove.Count == 1 ? "episode" : "episodes";
        var confirmed = new MenuBuilder<bool?>()
            .WithTitle($"Move [cyan]{episodesToMove.Count}[/] {episodeWord} from '[cyan]{Markup.Escape(sourceFeed.Title)}[/]' to '[cyan]{Markup.Escape(targetFeed.Title)}[/]'?")
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

        Out.BlankLine();

        // Move episodes
        var successCount = 0;
        var failureCount = 0;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Moving {episodesToMove.Count} {episodeWord}", maxValue: episodesToMove.Count);

                foreach (var episode in episodesToMove)
                {
                    var success = await EpisodeHelpers.MoveEpisodeAsync(httpClient, sourceFeed.Id, episode.Id, targetFeed.Id);
                    if (success)
                    {
                        successCount++;
                    }
                    else
                    {
                        failureCount++;
                    }
                    task.Increment(1);
                }
            });

        Out.BlankLine();

        // Summary
        if (successCount > 0)
        {
            Out.Success($"Moved {successCount} of {episodesToMove.Count} episode(s) successfully");
        }

        if (failureCount > 0)
        {
            Out.Error($"Failed to move {failureCount} episode(s)");
        }

        return failureCount == 0 ? 0 : 1;
    }
}
