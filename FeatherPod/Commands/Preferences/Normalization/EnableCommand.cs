using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences.Normalization;

internal sealed class EnableCommand : Command<NormalizationSettings>
{
    public override int Execute(CommandContext context, NormalizationSettings settings, CancellationToken cancellationToken)
    {
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        PreferencesHelpers.SetNormalizationEnabled(env, true);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] Audio normalization enabled for {env}");
        AnsiConsole.WriteLine();

        return 0;
    }
}
