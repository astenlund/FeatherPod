using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.AdminFeatures;

internal sealed class ShowCommand : Command<AdminFeaturesSettings>
{
    public override int Execute(CommandContext context, AdminFeaturesSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences - Admin Features[/]");
        Out.BlankLine();

        var enabled = PreferencesHelpers.GetEnableAdminFeatures();

        Out.MarkupLine($"Admin features: [cyan]{(enabled ?? false ? "enabled" : "disabled")}[/]{(enabled == null ? " [grey](default)[/]" : "")}");
        Out.WriteLineRaw();

        return 0;
    }
}
