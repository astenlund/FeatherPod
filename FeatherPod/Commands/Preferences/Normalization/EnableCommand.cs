using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;

using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.Normalization;

internal sealed class EnableCommand : Command<NormalizationSettings>
{
    public override int Execute(CommandContext context, NormalizationSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences - Enable Normalization[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        PreferencesHelpers.SetNormalizationEnabled(env, true);

        Out.BlankLine();
        Out.Success($"Audio normalization enabled for {env}");
        Out.BlankLine();

        return 0;
    }
}
