using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands;

internal sealed class InteractiveCommand : AsyncCommand<InteractiveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InteractiveSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Manager[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment, useDefault: true);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Main menu loop
        while (true)
        {
            AnsiConsole.WriteLine();

            var choice = ShowMenu();

            switch (choice)
            {
                case MenuChoice.List:
                    await CliHelpers.ListEpisodesAsync(httpClient);
                    break;

                case MenuChoice.Delete:
                    await CliHelpers.DeleteEpisodeAsync(httpClient);
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
                        }
                    }
                    else if (newEnv == null)
                    {
                        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                    }
                    break;

                case MenuChoice.Quit:
                    AnsiConsole.MarkupLine("[grey]Bye.[/]");
                    return 0;
            }
        }
    }

    private static MenuChoice ShowMenu()
    {
        return new MenuBuilder<MenuChoice>()
            .WithTitle("What would you like to do?")
            .WithHint("(arrow keys or L/D/E/Q)")
            .AddOption("L", "List episodes", MenuChoice.List)
            .AddOption("D", "Delete episode", MenuChoice.Delete)
            .AddOption("E", "Switch environment", MenuChoice.SwitchEnvironment)
            .AddOption("Q", "Quit", MenuChoice.Quit)
            .AllowCancel(false) // Don't allow escape on main menu
            .Show()!;
    }

    private enum MenuChoice
    {
        List,
        Delete,
        SwitchEnvironment,
        Quit
    }
}
