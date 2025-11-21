using FeatherPod.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.Normalization;

internal sealed class DisableCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
    {
        PreferencesHelpers.SetNormalizationEnabled(false);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓[/] Audio normalization disabled");
        AnsiConsole.WriteLine();

        return 0;
    }
}
