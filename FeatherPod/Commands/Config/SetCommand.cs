using FeatherPod.Infrastructure;
using FeatherPod.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Config;

internal sealed class SetCommand : Command<ConfigSetSettings>
{
    public override int Execute(CommandContext context, ConfigSetSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Configuration[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        if (!settings.Key.Equals("api-key", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]Unknown configuration key:[/] {Markup.Escape(settings.Key)}");
            AnsiConsole.MarkupLine("Available keys: [cyan]api-key[/]");

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Value))
        {
            AnsiConsole.MarkupLine("[red]API key cannot be empty.[/]");

            return 1;
        }

        ApiKeyHelpers.SaveApiKey(env, settings.Value);

        var filePath = ApiKeyHelpers.GetPreferencesPath();

        AnsiConsole.MarkupLine($"[green]✓[/] API key saved to [cyan]{filePath}[/]");

        return 0;
    }
}
