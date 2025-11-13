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

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Main menu loop
        while (true)
        {
            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .AddChoices("List episodes", "Delete episode", "Exit"));

            switch (choice)
            {
                case "List episodes":
                    await CliHelpers.ListEpisodesAsync(httpClient);
                    break;

                case "Delete episode":
                    await CliHelpers.DeleteEpisodeAsync(httpClient);
                    break;

                case "Exit":
                    AnsiConsole.MarkupLine("[grey]Bye.[/]");
                    return 0;
            }
        }
    }
}
