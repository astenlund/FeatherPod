using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.AdminFeatures;

internal sealed class DisableCommand : Command<AdminFeaturesSettings>
{
    public override int Execute(CommandContext context, AdminFeaturesSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences - Disable Admin Features[/]");
        Out.BlankLine();

        PreferencesHelpers.SetEnableAdminFeatures(false);

        Out.BlankLine();
        Out.Success("Admin features disabled");
        Out.BlankLine();
        Out.MarkupLine("[grey]Restart FeatherPod for changes to take effect.[/]");
        Out.WriteLineRaw();

        return 0;
    }
}
