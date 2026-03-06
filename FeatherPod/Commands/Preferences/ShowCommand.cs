using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences;

internal sealed class ShowCommand : Command<PreferencesShowSettings>
{
    public override int Execute(CommandContext context, PreferencesShowSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var apiKey = PreferencesHelpers.GetApiKey(env);
        var filePath = PreferencesHelpers.GetPreferencesPath();

        // Show API key status
        Out.MarkupLine(string.IsNullOrEmpty(apiKey)
            ? $"[yellow]API key ({env}):[/] (not configured)"
            : $"[bold]API key ({env}):[/] {PreferencesHelpers.MaskApiKey(apiKey)}");

        // Show normalization preference (defaults to enabled)
        var normPref = PreferencesHelpers.GetNormalizationEnabled(env);
        var normEnabled = normPref ?? true;

        Out.MarkupLine($"[bold]Audio normalization ({env}):[/] {(normEnabled ? "enabled" : "disabled")}{(normPref.HasValue ? "" : " (default)")}");

        // Show auto-connect preference (defaults to enabled)
        var autoConnectPref = PreferencesHelpers.GetAutoConnectEnabled(env);
        var autoConnectEnabled = autoConnectPref ?? true;

        Out.MarkupLine($"[bold]Auto-connect ({env}):[/] {(autoConnectEnabled ? "enabled" : "disabled")}{(autoConnectPref.HasValue ? "" : " (default)")}");

        // Show admin features preference (defaults to disabled, global not per-environment)
        var adminEnabled = PreferencesHelpers.GetEnableAdminFeatures();
        Out.MarkupLine($"[bold]Admin features:[/] {(adminEnabled ?? false ? "enabled" : "disabled")}{(adminEnabled == null ? " (default)" : "")}");

        Out.BlankLine();
        Out.MarkupLine($"[grey]Preferences: {Markup.Escape(filePath)}[/]");
        Out.BlankLine().Flush();

        return 0;
    }
}
