using FeatherPod.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.AutoConnect;

internal sealed class EnableCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
    {
        PreferencesHelpers.SetAutoConnectEnabled(true);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓[/] Auto-connect enabled");
        AnsiConsole.WriteLine();

        return 0;
    }
}
