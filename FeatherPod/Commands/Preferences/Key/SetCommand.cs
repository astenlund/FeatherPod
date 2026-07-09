using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.Key;

internal sealed class SetCommand : Command<KeySetSettings>
{
    public override int Execute(CommandContext context, KeySetSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences - Set API Key[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var key = settings.Key?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            Out.WriteLineRaw();
            key = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [bold]API key[/]:"));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            Out.Error("API key cannot be empty");

            return 1;
        }

        PreferencesHelpers.SaveApiKey(env, key);

        var prefsPath = PreferencesHelpers.GetPreferencesPath();

        Out.Success($"API key saved for {env}");
        Out.BlankLine();
        Out.MarkupLine($"[grey]Preferences: {Markup.Escape(prefsPath)}[/]");
        Out.WriteLineRaw();

        return 0;
    }
}
