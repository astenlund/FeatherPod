using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.AutoConnect;

internal sealed class EnableCommand : Command<AutoConnectSettings>
{
    public override int Execute(CommandContext context, AutoConnectSettings settings, CancellationToken cancellationToken)
    {
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        PreferencesHelpers.SetAutoConnectEnabled(env, true);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] Auto-connect enabled for {env}");
        AnsiConsole.WriteLine();

        return 0;
    }
}
