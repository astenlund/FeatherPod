using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.Normalization;

internal sealed class DisableCommand : Command<NormalizationSettings>
{
    public override int Execute(CommandContext context, NormalizationSettings settings, CancellationToken cancellationToken)
    {
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        PreferencesHelpers.SetNormalizationEnabled(env, false);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] Audio normalization disabled for {env}");
        AnsiConsole.WriteLine();

        return 0;
    }
}
