using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using EpisodeModel = FeatherPod.Shared.Models.Episode;
using FeedConfig = FeatherPod.Shared.Models.FeedConfig;

namespace FeatherPod.Commands.Episode;

internal sealed class MoveCommand : AsyncCommand<MoveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, MoveSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Move[/]");
        AnsiConsole.WriteLine();

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
                AnsiConsole.MarkupLine($"[red]✗[/] Source feed '{settings.FromFeed}' not found.");
                return 1;
            }
        }
        else
        {
            sourceFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true, contextMessage: "Select source feed:");
            if (sourceFeed == null)
            {
                AnsiConsole.MarkupLine("[red]✗[/] No feeds available.");
                return 1;
            }
        }

        // Get episodes from source feed
        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, sourceFeed.Id);
        if (episodes == null || episodes.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Feed '[cyan]{Markup.Escape(sourceFeed.Title)}[/]' has no episodes.[/]");
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
                AnsiConsole.MarkupLine($"[red]✗[/] No episodes match pattern '{settings.Episode}'");
                return 1;
            }

            AnsiConsole.MarkupLine($"Matched [bold]{episodesToMove.Count}[/] episode(s) from feed '[cyan]{Markup.Escape(sourceFeed.Title)}[/]'");
            AnsiConsole.WriteLine();
        }
        else
        {
            // Interactive mode: multi-select
            episodesToMove = EpisodeHelpers.SelectEpisodesMulti(episodes);
            if (episodesToMove.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 1;
            }
            AnsiConsole.WriteLine();
        }

        // Determine target feed
        FeedConfig? targetFeed;
        if (!string.IsNullOrEmpty(settings.ToFeed))
        {
            targetFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, settings.ToFeed);
            if (targetFeed == null)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Target feed '{settings.ToFeed}' not found.");
                return 1;
            }
        }
        else
        {
            // Get all feeds except the source for selection
            var allFeeds = await FeedHelpers.GetFeedsAsync(httpClient);
            if (allFeeds.Count <= 1)
            {
                AnsiConsole.MarkupLine("[red]✗[/] No other feeds available. Create another feed first.");
                return 1;
            }

            var targetFeeds = allFeeds.Where(f => f.Id != sourceFeed.Id).ToList();
            if (targetFeeds.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]✗[/] No other feeds available.");
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
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 1;
            }
            AnsiConsole.WriteLine();
        }

        // Validate: source != target
        if (sourceFeed.Id == targetFeed.Id)
        {
            AnsiConsole.MarkupLine("[red]✗[/] Cannot move episodes within the same feed.");
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
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 1;
        }

        AnsiConsole.WriteLine();

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

        AnsiConsole.WriteLine();

        // Summary
        if (successCount > 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Moved {successCount} of {episodesToMove.Count} episode(s) successfully");
        }

        if (failureCount > 0)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to move {failureCount} episode(s)");
        }

        return failureCount == 0 ? 0 : 1;
    }
}
