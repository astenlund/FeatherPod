using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;

using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.Normalization;

internal sealed class ShowCommand : Command<NormalizationSettings>
{
    public override int Execute(CommandContext context, NormalizationSettings settings, CancellationToken cancellationToken)
    {
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var pref = PreferencesHelpers.GetNormalizationEnabled(env);
        var enabled = pref ?? true;

        Out.BlankLine();
        Out.MarkupLine($"[bold]Audio normalization ({env}):[/] {(enabled ? "enabled" : "disabled")}{(pref.HasValue ? "" : " (default)")}");
        Out.BlankLine().Flush();

        return 0;
    }
}
