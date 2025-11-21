using FeatherPod.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.AutoConnect;

internal sealed class ShowCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
    {
        var pref = PreferencesHelpers.GetAutoConnectEnabled();
        var enabled = pref ?? true;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Auto-connect:[/] {(enabled ? "enabled" : "disabled")}{(pref.HasValue ? "" : " (default)")}");
        AnsiConsole.WriteLine();

        return 0;
    }
}
