using FeatherPod.Infrastructure;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

using EpisodeModel = FeatherPod.Shared.Models.Episode;

namespace FeatherPod.Commands.Episode;

internal sealed class RenameCommand : AsyncCommand<RenameSettings>
{
    private enum RenameApproach { EditCurrentTitle, GetAiSuggestion, EditSuggestion, EditProvidedTitle }

    /// <summary>
    /// Core rename operation - can be called from CLI or InteractiveCommand.
    /// </summary>
    public static async Task<EpisodeOperationResult> RenameEpisodeAsync(
        HttpClient httpClient,
        string feedId,
        EpisodeModel episode,
        string newTitle,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(episode.Title, newTitle, StringComparison.Ordinal))
        {
            Out.MarkupLine("[grey]Title unchanged.[/]");

            return new() { Success = true, EpisodeId = episode.Id };
        }

        var result = await EpisodeHelpers.UpdateEpisodeTitleAsync(httpClient, feedId, episode.Id, newTitle, cancellationToken);

        if (result.Success)
        {
            Out.Success($"Renamed: [cyan]{Markup.Escape(episode.Title)}[/] → [cyan]{Markup.Escape(newTitle)}[/]");
        }

        return result;
    }

    /// <summary>
    /// Interactive title resolution for the "no flags" case.
    /// Offers AI suggestion or manual title entry.
    /// Can be called from InteractiveCommand.
    /// </summary>
    internal static async Task<string?> ResolveNewTitleInteractiveAsync(
        HttpClient httpClient, string feedId, EpisodeModel episode,
        CancellationToken cancellationToken)
    {
        var choice = new MenuBuilder<RenameApproach?>()
            .WithTitle("How would you like to rename this episode?")
            .WithHint("(arrow keys or highlighted letter, Esc to cancel)")
            .AddOption("C", "Edit current title", RenameApproach.EditCurrentTitle)
            .AddOption("S", "Get suggestion", RenameApproach.GetAiSuggestion)
            .AllowCancel()
            .Show();

        if (choice == null)
        {
            return null;
        }

        if (choice == RenameApproach.EditCurrentTitle)
        {
            return PromptForTitle(episode.Title);
        }

        // AI path: fetch suggestion, then let user edit it
        var suggestion = await FetchSuggestionWithSpinnerAsync(httpClient, feedId, episode, cancellationToken);

        if (suggestion == null)
        {
            Out.Warning("AI suggestion unavailable. Editing current title instead.");
            Out.BlankLine();

            return PromptForTitle(episode.Title);
        }

        Out.MarkupLine($"  Suggestion: [cyan]{Markup.Escape(suggestion)}[/]");
        Out.BlankLine();

        return PromptForTitle(suggestion);
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RenameSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode Rename[/]");
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
                ? $"Feed '{Markup.Escape(settings.FeedId)}' not found."
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

        // Find episode by ID or select interactively
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
            episode = EpisodeHelpers.SelectEpisodeSingle(episodes);
            if (episode == null)
            {
                Out.Cancelled();
                Out.BlankLine().Flush();

                return 1;
            }
        }

        Out.BlankLine();
        Out.MarkupLine($"  Current title: [cyan]{Markup.Escape(episode.Title)}[/]");
        Out.MarkupLine($"  Filename: [grey]{Markup.Escape(episode.FileName)}[/]");
        Out.BlankLine();

        // Determine new title based on flag combination
        // -t only: auto-accept without prompting
        string? newTitle;
        if (!string.IsNullOrWhiteSpace(settings.NewTitle) && !settings.Suggest)
        {
            newTitle = settings.NewTitle;
        }
        else
        {
            newTitle = await ResolveNewTitleAsync(httpClient, feed.Id, episode, settings, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(newTitle))
        {
            Out.Cancelled();
            Out.BlankLine().Flush();

            return 1;
        }

        var result = await RenameEpisodeAsync(httpClient, feed.Id, episode, newTitle.Trim(), cancellationToken);
        Out.BlankLine().Flush();

        return result.Success ? 0 : 1;
    }

    private static async Task<string?> ResolveNewTitleAsync(
        HttpClient httpClient, string feedId, EpisodeModel episode,
        RenameSettings settings, CancellationToken cancellationToken)
    {
        // --suggest only: fetch suggestion, then let user choose which to edit
        if (settings.Suggest && string.IsNullOrEmpty(settings.NewTitle))
        {
            var suggestion = await FetchSuggestionWithSpinnerAsync(httpClient, feedId, episode, cancellationToken);

            if (suggestion == null)
            {
                Out.Warning("AI suggestion unavailable. Editing current title instead.");
                Out.BlankLine();

                return PromptForTitle(episode.Title);
            }

            Out.MarkupLine($"  Suggestion: [cyan]{Markup.Escape(suggestion)}[/]");
            Out.BlankLine();

            return ShowSuggestionMenu(suggestion, episode.Title, providedTitle: null);
        }

        // -t + --suggest: fetch suggestion, then let user choose which to edit
        if (settings.Suggest && !string.IsNullOrEmpty(settings.NewTitle))
        {
            var suggestion = await FetchSuggestionWithSpinnerAsync(httpClient, feedId, episode, cancellationToken);

            if (suggestion == null)
            {
                Out.Warning("AI suggestion unavailable. Using provided title.");

                return settings.NewTitle;
            }

            Out.MarkupLine($"  Your title: [cyan]{Markup.Escape(settings.NewTitle)}[/]");
            Out.MarkupLine($"  Suggestion: [cyan]{Markup.Escape(suggestion)}[/]");
            Out.BlankLine();

            return ShowSuggestionMenu(suggestion, episode.Title, providedTitle: settings.NewTitle);
        }

        // Neither flag: interactive flow
        return await ResolveNewTitleInteractiveAsync(httpClient, feedId, episode, cancellationToken);
    }

    private static async Task<string?> FetchSuggestionWithSpinnerAsync(
        HttpClient httpClient, string feedId, EpisodeModel episode,
        CancellationToken cancellationToken)
    {
        string? suggestion = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[bold]Fetching AI suggestion...[/]", async _ =>
            {
                suggestion = await EpisodeHelpers.SuggestTitleAsync(httpClient, feedId, episode.Id, cancellationToken);
            });

        return suggestion;
    }

    private static string? ShowSuggestionMenu(string suggestion, string currentTitle, string? providedTitle)
    {
        var menu = new MenuBuilder<RenameApproach?>()
            .WithTitle("Choose a title to edit:")
            .WithHint("(arrow keys or highlighted letter, Esc to cancel)");

        if (providedTitle != null)
        {
            menu.AddOption("Y", "Edit your title", RenameApproach.EditProvidedTitle);
        }
        else
        {
            menu.AddOption("C", "Edit current title", RenameApproach.EditCurrentTitle);
        }

        menu.AddOption("S", "Edit suggestion", RenameApproach.EditSuggestion);
        menu.AllowCancel();

        var choice = menu.Show();

        return choice switch
        {
            RenameApproach.EditSuggestion => PromptForTitle(suggestion),
            RenameApproach.EditProvidedTitle => PromptForTitle(providedTitle),
            RenameApproach.EditCurrentTitle => PromptForTitle(currentTitle),
            _ => null,
        };
    }

    private static string? PromptForTitle(string? defaultValue)
    {
        Out.BlankLine();

        return LineEditor.Edit("New title: ", defaultValue ?? "");
    }
}
