using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.ApiKey;

internal sealed class SetCommand : Command<ApiKeySetSettings>
{
    public override int Execute(CommandContext context, ApiKeySetSettings settings, CancellationToken cancellationToken)
    {
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var key = settings.Key.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            Out.Error("API key cannot be empty");

            return 1;
        }

        PreferencesHelpers.SaveApiKey(env, key);

        var prefsPath = PreferencesHelpers.GetPreferencesPath();

        Out.BlankLine();
        Out.Success($"API key saved for {env}");
        Out.MarkupLine($"[grey]Preferences: {Markup.Escape(prefsPath)}[/]");
        Out.BlankLine();

        return 0;
    }
}
