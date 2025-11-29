using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using EpisodeModel = FeatherPod.Shared.Models.Episode;
using FeedConfig = FeatherPod.Shared.Models.FeedConfig;

namespace FeatherPod.Commands.Episode;

internal sealed class CopyCommand : AsyncCommand<CopySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CopySettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Copy[/]");
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

        // Determine which episodes to copy
        List<EpisodeModel> episodesToCopy;
        if (!string.IsNullOrEmpty(settings.Episode))
        {
            // CLI mode: use pattern matching
            episodesToCopy = EpisodeHelpers.MatchEpisodesByPattern(episodes, settings.Episode);
            if (episodesToCopy.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] No episodes match pattern '{settings.Episode}'");
                return 1;
            }

            AnsiConsole.MarkupLine($"Matched [bold]{episodesToCopy.Count}[/] episode(s) from feed '[cyan]{Markup.Escape(sourceFeed.Title)}[/]'");
            AnsiConsole.WriteLine();
        }
        else
        {
            // Interactive mode: multi-select
            episodesToCopy = EpisodeHelpers.SelectEpisodesMulti(episodes);
            if (episodesToCopy.Count == 0)
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
            // Get all feeds (can copy to same feed - creates duplicate)
            var allFeeds = await FeedHelpers.GetFeedsAsync(httpClient);
            if (allFeeds.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]✗[/] No feeds available.");
                return 1;
            }

            var menu = new MenuBuilder<FeedConfig?>()
                .WithTitle("Select target feed:")
                .AllowCancel();

            foreach (var feed in allFeeds)
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

        // Confirmation
        var episodeWord = episodesToCopy.Count == 1 ? "episode" : "episodes";
        var actionDescription = sourceFeed.Id == targetFeed.Id
            ? $"Duplicate [cyan]{episodesToCopy.Count}[/] {episodeWord} in '[cyan]{Markup.Escape(sourceFeed.Title)}[/]'?"
            : $"Copy [cyan]{episodesToCopy.Count}[/] {episodeWord} from '[cyan]{Markup.Escape(sourceFeed.Title)}[/]' to '[cyan]{Markup.Escape(targetFeed.Title)}[/]'?";

        var confirmed = new MenuBuilder<bool?>()
            .WithTitle(actionDescription)
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

        // Copy episodes
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
                var task = ctx.AddTask($"Copying {episodesToCopy.Count} {episodeWord}", maxValue: episodesToCopy.Count);

                foreach (var episode in episodesToCopy)
                {
                    var success = await EpisodeHelpers.CopyEpisodeAsync(httpClient, sourceFeed.Id, episode.Id, targetFeed.Id);
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
            AnsiConsole.MarkupLine($"[green]✓[/] Copied {successCount} of {episodesToCopy.Count} episode(s) successfully");
        }

        if (failureCount > 0)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to copy {failureCount} episode(s)");
        }

        return failureCount == 0 ? 0 : 1;
    }
}
