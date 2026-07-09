using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.Key;

internal sealed class ShowCommand : Command<KeyShowSettings>
{
    public override int Execute(CommandContext context, KeyShowSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences - Show API Key[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var apiKey = PreferencesHelpers.GetApiKey(env);

        Out.BlankLine();
        Out.MarkupLine(
            string.IsNullOrEmpty(apiKey)
                ? $"[yellow]API key ({Markup.Escape(env)}):[/] (not configured)"
                : $"[bold]API key ({Markup.Escape(env)}):[/] {PreferencesHelpers.MaskApiKey(apiKey)}");
        Out.BlankLine();

        return 0;
    }
}
