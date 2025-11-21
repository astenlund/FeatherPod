using FeatherPod.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.Normalization;

internal sealed class ShowCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken cancellationToken)
    {
        var pref = PreferencesHelpers.GetNormalizationEnabled();
        var enabled = pref ?? true;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Audio normalization:[/] {(enabled ? "enabled" : "disabled")}{(pref.HasValue ? "" : " (default)")}");
        AnsiConsole.WriteLine();

        return 0;
    }
}
