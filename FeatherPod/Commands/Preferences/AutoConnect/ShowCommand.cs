using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.AutoConnect;

internal sealed class ShowCommand : Command<AutoConnectSettings>
{
    public override int Execute(CommandContext context, AutoConnectSettings settings, CancellationToken cancellationToken)
    {
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var pref = PreferencesHelpers.GetAutoConnectEnabled(env);
        var enabled = pref ?? true;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Auto-connect ({env}):[/] {(enabled ? "enabled" : "disabled")}{(pref.HasValue ? "" : " (default)")}");
        AnsiConsole.WriteLine();

        return 0;
    }
}
