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

        var apiKey = PreferencesHelpers.GetApiKey(env);
        var filePath = PreferencesHelpers.GetPreferencesPath();

        // Show API key status
        if (string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine($"[yellow]API Key:[/] (not configured)");
        }
        else
        {
            AnsiConsole.MarkupLine($"[cyan]API Key:[/] {PreferencesHelpers.MaskApiKey(apiKey)}");
        }

        // Show normalization preference (defaults to enabled)
        var normPref = PreferencesHelpers.GetNormalizationEnabled();
        var normEnabled = normPref ?? true;
        AnsiConsole.MarkupLine($"[cyan]Normalization:[/] {(normEnabled ? "enabled" : "disabled")}{(normPref.HasValue ? "" : " (default)")}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]Preferences: {filePath}[/]");
        AnsiConsole.WriteLine();

        return 0;
    }
}
