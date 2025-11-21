using FeatherPod.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.Normalization;

internal sealed class EnableCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
    {
        PreferencesHelpers.SetNormalizationEnabled(true);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓[/] Audio normalization enabled");
        AnsiConsole.WriteLine();

        return 0;
    }
}
