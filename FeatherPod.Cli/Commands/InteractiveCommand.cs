using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings;
using FeatherPod.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands;

internal sealed class InteractiveCommand : AsyncCommand<InteractiveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InteractiveSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment, useDefault: true);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Select initial feed
        var currentFeed = await CliHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
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
                        await CliHelpers.ListEpisodesAsync(httpClient, currentFeed);
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
                        await CliHelpers.DeleteEpisodeAsync(httpClient, currentFeed);
                    }
                    break;

                case MenuChoice.SwitchFeed:
                    var newFeed = await CliHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
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
                    await CliHelpers.ManageFeedsAsync(httpClient);
                    // Refresh current feed in case it was deleted or renamed
                    if (currentFeed != null)
                    {
                        var feeds = await CliHelpers.GetFeedsAsync(httpClient);
                        currentFeed = feeds.FirstOrDefault(f => f.Id == currentFeed.Id);
                    }
                    currentFeed ??= await CliHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
                    break;

                case MenuChoice.SwitchEnvironment:
                    var newEnv = CliHelpers.SelectEnvironment();
                    if (newEnv != null && newEnv != env)
                    {
                        env = newEnv;
                        AnsiConsole.Clear();
                        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine($"Environment: [cyan]{env}[/]");
                        AnsiConsole.WriteLine();

                        var (newClient, _) = await CliHelpers.SetupHttpClientAsync(env);
                        if (newClient != null)
                        {
                            httpClient = newClient;
                            // Select feed for new environment
                            currentFeed = await CliHelpers.SelectFeedAsync(httpClient, env, forcePrompt: false);
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
            ? $"Feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]\nWhat would you like to do?"
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
