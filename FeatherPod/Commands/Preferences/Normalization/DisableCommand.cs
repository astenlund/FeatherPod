using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;

using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

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

        Out.BlankLine();
        Out.Success($"Audio normalization disabled for {env}");
        Out.BlankLine();

        return 0;
    }
}
