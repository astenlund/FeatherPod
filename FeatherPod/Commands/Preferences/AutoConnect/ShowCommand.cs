using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;

using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.AutoConnect;

internal sealed class ShowCommand : Command<AutoConnectSettings>
{
    public override int Execute(CommandContext context, AutoConnectSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences - Auto-Connect[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var pref = PreferencesHelpers.GetAutoConnectEnabled(env);
        var enabled = pref ?? true;

        Out.BlankLine();
        Out.MarkupLine($"[bold]Auto-connect ({env}):[/] {(enabled ? "enabled" : "disabled")}{(pref.HasValue ? "" : " (default)")}");
        Out.BlankLine().Flush();

        return 0;
    }
}
