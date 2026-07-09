using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;

using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.AutoConnect;

internal sealed class DisableCommand : Command<AutoConnectSettings>
{
    public override int Execute(CommandContext context, AutoConnectSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences - Disable Auto-Connect[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        PreferencesHelpers.SetAutoConnectEnabled(env, false);

        Out.BlankLine();
        Out.Success($"Auto-connect disabled for {env}");
        Out.BlankLine();

        return 0;
    }
}
