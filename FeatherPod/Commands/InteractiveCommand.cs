using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;
using FeatherPod.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

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
                        await EpisodeHelpers.ListEpisodesAsync(httpClient, currentFeed);
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
                        var deleted = await EpisodeHelpers.DeleteEpisodeInteractiveAsync(httpClient, currentFeed);
                        if (deleted.HasValue) // Not cancelled
                        {
                            WaitForKeyPress();
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
                    var result = await FeedHelpers.ManageFeedsAsync(httpClient);

                    // Handle created feed
                    if (result.CreatedFeed != null)
                    {
                        currentFeed = result.CreatedFeed;
                        AnsiConsole.MarkupLine($"Switched to feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
                        WaitForKeyPress();
                    }
                    // Handle renamed feed
                    else if (result.RenamedFeed != null)
                    {
                        // If we were on the renamed feed, follow it
                        if (currentFeed?.Id == result.OldFeedId)
                        {
                            currentFeed = result.RenamedFeed;
                            AnsiConsole.MarkupLine($"Switched to renamed feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
                        }
                        WaitForKeyPress();
                    }
                    // Handle deleted feed
                    else if (result.DeletedFeedId != null)
                    {
                        // If we were on the deleted feed, clear it and prompt for a new one
                        if (currentFeed?.Id == result.DeletedFeedId)
                        {
                            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, contextMessage: "Previous feed was deleted.");
                        }
                        WaitForKeyPress();
                    }
                    else
                    {
                        // User cancelled or error - refresh current feed
                        if (currentFeed != null)
                        {
                            var feeds = await FeedHelpers.GetFeedsAsync(httpClient);
                            currentFeed = feeds.FirstOrDefault(f => f.Id == currentFeed.Id);
                        }
                        currentFeed ??= await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
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
            .AddOption("F", "Switch feed", MenuChoice.SwitchFeed)
            .AddOption("M", "Manage feeds", MenuChoice.ManageFeeds)
            .AddOption("E", "Switch environment", MenuChoice.SwitchEnvironment)
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
        SwitchEnvironment,
        Quit
    }
}
