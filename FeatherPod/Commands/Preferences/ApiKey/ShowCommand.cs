using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.ApiKey;

internal sealed class ShowCommand : Command<ApiKeyShowSettings>
{
    public override int Execute(CommandContext context, ApiKeyShowSettings settings, CancellationToken cancellationToken)
    {
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var apiKey = PreferencesHelpers.GetApiKey(env);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            string.IsNullOrEmpty(apiKey)
                ? $"[yellow]API key ({env}):[/] (not configured)"
                : $"[bold]API key ({env}):[/] {PreferencesHelpers.MaskApiKey(apiKey)}");
        AnsiConsole.WriteLine();

        return 0;
    }
}
