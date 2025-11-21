using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;
using FeatherPod.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

using EpisodeDeleteCommand = FeatherPod.Commands.Episode.DeleteCommand;
using EpisodeListCommand = FeatherPod.Commands.Episode.ListCommand;
using FeedUpdateCommand = FeatherPod.Commands.Feed.UpdateCommand;
using FeedCreateCommand = FeatherPod.Commands.Feed.CreateCommand;
using FeedDeleteCommand = FeatherPod.Commands.Feed.DeleteCommand;
using FeedRenameCommand = FeatherPod.Commands.Feed.RenameCommand;

namespace FeatherPod.Commands;

internal sealed class InteractiveCommand : AsyncCommand<InteractiveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InteractiveSettings settings, CancellationToken cancellationToken)
    {
        // Clear screen before any output
        Console.Write("\e[2J\e[H");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment, useDefault: true);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Select initial feed
        var currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
        if (currentFeed == null)
        {
            AnsiConsole.MarkupLine("[yellow]No feeds available. Create one using 'M: Manage Feeds'.[/]");
            AnsiConsole.WriteLine();
        }

        // Skip clear on first iteration (header already shown from setup)
        var skipClear = true;

        // Main menu loop
        while (true)
        {
            if (!skipClear)
            {
                // Clear screen and redraw header
                Console.Write("\e[2J\e[H");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"Environment: [cyan]{env}[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"API: [cyan]{httpClient.BaseAddress}[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[green]✓[/] Connected");
                AnsiConsole.WriteLine();
            }
            skipClear = false;

            var choice = ShowMenu(currentFeed);

            switch (choice)
            {
                case MenuChoice.List:
                    if (currentFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feed selected. Use 'M: Manage Feeds' to create one.[/]");
                        AnsiConsole.WriteLine();
                    }
                    else
                    {
                        await EpisodeListCommand.ListEpisodesAsync(httpClient, currentFeed);
                    }
                    WaitForKeyPress();
                    break;

                case MenuChoice.Delete:
                    if (currentFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feed selected. Use 'M: Manage Feeds' to create one.[/]");
                        WaitForKeyPress();
                    }
                    else
                    {
                        var episodes = await EpisodeHelpers.GetEpisodesAsync(httpClient, currentFeed.Id);
                        if (episodes == null || episodes.Count == 0)
                        {
                            AnsiConsole.MarkupLine("[yellow]No episodes to delete.[/]");
                            WaitForKeyPress();
                        }
                        else
                        {
                            var selected = EpisodeHelpers.SelectEpisodesMulti(episodes);
                            if (selected.Count == 1)
                            {
                                await EpisodeDeleteCommand.DeleteEpisodeAsync(httpClient, currentFeed.Id, selected[0], cancellationToken: cancellationToken);
                                WaitForKeyPress();
                            }
                            else if (selected.Count > 1)
                            {
                                AnsiConsole.MarkupLine("[yellow]Multiple episodes selected. Delete one at a time in interactive mode.[/]");
                                WaitForKeyPress();
                            }
                        }
                    }
                    break;

                case MenuChoice.SwitchFeed:
                    var newFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                    if (newFeed != null)
                    {
                        currentFeed = newFeed;
                    }
                    // No pause - feed title shows in menu header
                    break;

                case MenuChoice.ManageFeeds:
                    var manageChoice = new MenuBuilder<string?>()
                        .WithTitle("Manage Feeds:")
                        .WithHint("(arrow keys or C/U/R/D, Esc to go back)")
                        .AddOption("C", "Create new feed", "create")
                        .AddOption("U", "Update feed metadata", "update")
                        .AddOption("R", "Rename feed ID", "rename")
                        .AddOption("D", "Delete feed", "delete")
                        .AllowCancel()
                        .Show();

                    switch (manageChoice)
                    {
                        case "create":
                            // Prompt for feed details
                            var id = AnsiConsole.Ask<string>("Feed [cyan]ID[/] (URL-friendly slug):");
                            var title = AnsiConsole.Ask<string>("Feed [cyan]title[/]:");
                            var author = AnsiConsole.Ask<string>("Feed [cyan]author[/]:");
                            var description = AnsiConsole.Ask("Description (optional):", string.Empty);
                            var summary = AnsiConsole.Ask("Summary (optional, defaults to description):", string.Empty);
                            var email = AnsiConsole.Ask("Email (optional):", string.Empty);
                            var language = AnsiConsole.Ask("Language:", "en");
                            var category = AnsiConsole.Ask("Category (optional):", string.Empty);

                            var feedConfig = new FeedConfig
                            {
                                Id = id.Trim(),
                                Title = title.Trim(),
                                Description = string.IsNullOrEmpty(description) ? null : description.Trim(),
                                Summary = string.IsNullOrEmpty(summary) ? null : summary.Trim(),
                                Author = author.Trim(),
                                Email = string.IsNullOrEmpty(email) ? null : email.Trim(),
                                Language = language.Trim(),
                                Category = string.IsNullOrEmpty(category) ? null : category.Trim()
                            };

                            var createResult = await FeedCreateCommand.CreateFeedAsync(httpClient, feedConfig, cancellationToken: cancellationToken);
                            if (createResult.Success)
                            {
                                currentFeed = createResult.Feed;
                                AnsiConsole.MarkupLine($"Switched to feed: [cyan]{Markup.Escape(currentFeed!.Title)}[/]");
                            }
                            WaitForKeyPress();
                            break;

                        case "update":
                            var feedToUpdate = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                            if (feedToUpdate != null)
                            {
                                await FeedUpdateCommand.UpdateFeedInteractiveAsync(httpClient, feedToUpdate, cancellationToken);
                                // Refresh current feed if it was updated
                                if (currentFeed?.Id == feedToUpdate.Id)
                                {
                                    currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, feedToUpdate.Id);
                                }
                                WaitForKeyPress();
                            }
                            break;

                        case "rename":
                            var feedToRename = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                            if (feedToRename != null)
                            {
                                var newId = AnsiConsole.Ask<string>("New feed [cyan]ID[/]:");
                                var renameResult = await FeedRenameCommand.RenameFeedAsync(httpClient, feedToRename.Id, newId.Trim(), cancellationToken);
                                if (renameResult.Success && currentFeed?.Id == renameResult.OldFeedId)
                                {
                                    // Follow the rename
                                    currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, renameResult.FeedId!);
                                    AnsiConsole.MarkupLine($"Switched to renamed feed: [cyan]{Markup.Escape(currentFeed?.Title ?? renameResult.FeedId!)}[/]");
                                }
                                WaitForKeyPress();
                            }
                            break;

                        case "delete":
                            var feedToDelete = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                            if (feedToDelete != null)
                            {
                                var deleteResult = await FeedDeleteCommand.DeleteFeedAsync(httpClient, feedToDelete.Id, cancellationToken: cancellationToken);
                                if (deleteResult.Success && currentFeed?.Id == deleteResult.FeedId)
                                {
                                    // Current feed was deleted, select a new one
                                    currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, contextMessage: "Previous feed was deleted.");
                                }
                                WaitForKeyPress();
                            }
                            break;
                    }
                    break;

                case MenuChoice.SwitchEnvironment:
                    var newEnv = EnvironmentHelpers.SelectEnvironment();
                    if (newEnv != null && newEnv != env)
                    {
                        env = newEnv;

                        // Clear screen and show connection progress
                        Console.Write("\e[2J\e[H");
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
                        AnsiConsole.WriteLine();

                        var (newClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
                        if (newClient != null)
                        {
                            httpClient = newClient;
                            // Select feed for new environment
                            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
                        }

                        // Skip clear on next iteration (header already shown)
                        skipClear = true;
                    }
                    break;

                case MenuChoice.Settings:
                    var settingsChoice = new MenuBuilder<string?>()
                        .WithTitle("Settings:")
                        .WithHint("(arrow keys or N, Esc to go back)")
                        .AddOption("N", "Audio normalization", "normalization")
                        .AllowCancel()
                        .Show();

                    if (settingsChoice == "normalization")
                    {
                        var currentNorm = PreferencesHelpers.GetNormalizationEnabled() ?? true;
                        var normChoice = new MenuBuilder<bool?>()
                            .WithTitle($"Audio normalization is currently {(currentNorm ? "enabled" : "disabled")}:")
                            .WithHint("(arrow keys or E/D, Esc to cancel)")
                            .AddOption("E", "Enable normalization", true)
                            .AddOption("D", "Disable normalization", false)
                            .AllowCancel()
                            .Show();

                        if (normChoice.HasValue)
                        {
                            PreferencesHelpers.SetNormalizationEnabled(normChoice.Value);
                            AnsiConsole.MarkupLine($"[green]✓[/] Audio normalization {(normChoice.Value ? "enabled" : "disabled")}");
                            WaitForKeyPress();
                        }
                    }
                    break;

                case MenuChoice.Quit:
                    AnsiConsole.MarkupLine("[grey]Bye.[/]");
                    AnsiConsole.WriteLine();
                    return 0;
            }
        }
    }

    private static void WaitForKeyPress()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Markup("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }

    private static MenuChoice ShowMenu(FeedConfig? currentFeed)
    {
        if (currentFeed != null)
        {
            AnsiConsole.MarkupLine($"Feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
            AnsiConsole.WriteLine();
        }

        return new MenuBuilder<MenuChoice>()
            .WithTitle("What would you like to do?")
            .WithHint("(arrow keys or highlighted letter)")
            .AddOption("L", "List episodes", MenuChoice.List)
            .AddOption("D", "Delete episode", MenuChoice.Delete)
            .AddOption("M", "Manage feeds", MenuChoice.ManageFeeds)
            .AddOption("F", "Switch feed", MenuChoice.SwitchFeed)
            .AddOption("E", "Environment", MenuChoice.SwitchEnvironment)
            .AddOption("S", "Settings", MenuChoice.Settings)
            .AddOption("Q", "Quit", MenuChoice.Quit)
            .AllowCancel(false) // Don't allow escape on main menu
            .Show();
    }

    private enum MenuChoice
    {
        List,
        Delete,
        SwitchFeed,
        ManageFeeds,
        Settings,
        SwitchEnvironment,
        Quit
    }
}
