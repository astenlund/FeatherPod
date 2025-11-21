using FeatherPod.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.AutoConnect;

internal sealed class DisableCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
    {
        PreferencesHelpers.SetAutoConnectEnabled(false);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓[/] Auto-connect disabled");
        AnsiConsole.WriteLine();

        return 0;
    }
}
