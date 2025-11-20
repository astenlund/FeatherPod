using FeatherPod.Infrastructure;
using FeatherPod.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Config;

internal sealed class ShowCommand : Command<ConfigShowSettings>
{
    public override int Execute(CommandContext context, ConfigShowSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Configuration[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var apiKey = ApiKeyHelpers.GetApiKey(env);
        var filePath = ApiKeyHelpers.GetLocalSettingsPath(env);

        if (string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine($"[yellow]API Key:[/] (not configured)");
            AnsiConsole.MarkupLine($"[grey]File: {filePath}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"Run [cyan]FeatherPod config set api-key <your-key>[/] to configure.");
        }
        else
        {
            AnsiConsole.MarkupLine($"[cyan]API Key:[/] {ApiKeyHelpers.MaskApiKey(apiKey)}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[grey]File: {filePath}[/]");
        }

        AnsiConsole.WriteLine();

        return 0;
    }
}
