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

        // Main menu loop
        while (true)
        {
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
                    break;

                case MenuChoice.Delete:
                    if (currentFeed == null)
                    {
                        AnsiConsole.MarkupLine("[yellow]No feed selected. Use 'M: Manage Feeds' to create one.[/]");
                        AnsiConsole.WriteLine();
                    }
                    else
                    {
                        await EpisodeHelpers.DeleteEpisodeAsync(httpClient, currentFeed);
                    }
                    break;

                case MenuChoice.SwitchFeed:
                    var newFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
                    if (newFeed != null)
                    {
                        currentFeed = newFeed;
                        AnsiConsole.MarkupLine($"Switched to feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
                        AnsiConsole.WriteLine();
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        AnsiConsole.WriteLine();
                    }
                    break;

                case MenuChoice.ManageFeeds:
                    var result = await FeedHelpers.ManageFeedsAsync(httpClient);

                    // Handle created feed
                    if (result.CreatedFeed != null)
                    {
                        currentFeed = result.CreatedFeed;
                        AnsiConsole.MarkupLine($"Switched to feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
                        AnsiConsole.WriteLine();
                    }
                    // Handle renamed feed
                    else if (result.RenamedFeed != null)
                    {
                        // If we were on the renamed feed, follow it
                        if (currentFeed?.Id == result.OldFeedId)
                        {
                            currentFeed = result.RenamedFeed;
                            AnsiConsole.MarkupLine($"Switched to renamed feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
                            AnsiConsole.WriteLine();
                        }
                    }
                    // Handle deleted feed
                    else if (result.DeletedFeedId != null)
                    {
                        // If we were on the deleted feed, clear it and prompt for a new one
                        if (currentFeed?.Id == result.DeletedFeedId)
                        {
                            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false, contextMessage: "Previous feed was deleted.");
                        }
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
                        AnsiConsole.Clear();
                        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"Environment: [cyan]{env}[/]");
                        AnsiConsole.WriteLine();

                        var (newClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
                        if (newClient != null)
                        {
                            httpClient = newClient;
                            // Select feed for new environment
                            currentFeed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
                        }
                    }
                    else if (newEnv != null)
                    {
                        // Same environment selected - show same output for consistency
                        AnsiConsole.MarkupLine($"Environment: [cyan]{env}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                        AnsiConsole.WriteLine();

                    }
                    break;

                case MenuChoice.Quit:
                    AnsiConsole.MarkupLine("[grey]Bye.[/]");
                    AnsiConsole.WriteLine();
                    return 0;
            }
        }
    }

    private static MenuChoice ShowMenu(FeedConfig? currentFeed)
    {
        var title = currentFeed != null
            ? $"Feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]\n\nWhat would you like to do?"
            : "What would you like to do?";

        return new MenuBuilder<MenuChoice>()
            .WithTitle(title)
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
