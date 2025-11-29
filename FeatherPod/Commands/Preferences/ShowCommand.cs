using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Preferences;

internal sealed class ShowCommand : Command<PreferencesShowSettings>
{
    public override int Execute(CommandContext context, PreferencesShowSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Preferences[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var apiKey = PreferencesHelpers.GetApiKey(env);
        var filePath = PreferencesHelpers.GetPreferencesPath();

        // Show API key status
        AnsiConsole.MarkupLine(string.IsNullOrEmpty(apiKey)
            ? $"[yellow]API key ({env}):[/] (not configured)"
            : $"[cyan]API key ({env}):[/] {PreferencesHelpers.MaskApiKey(apiKey)}");

        // Show normalization preference (defaults to enabled)
        var normPref = PreferencesHelpers.GetNormalizationEnabled(env);
        var normEnabled = normPref ?? true;

        AnsiConsole.MarkupLine($"[cyan]Audio normalization ({env}):[/] {(normEnabled ? "enabled" : "disabled")}{(normPref.HasValue ? "" : " (default)")}");

        // Show auto-connect preference (defaults to enabled)
        var autoConnectPref = PreferencesHelpers.GetAutoConnectEnabled(env);
        var autoConnectEnabled = autoConnectPref ?? true;

        AnsiConsole.MarkupLine($"[cyan]Auto-connect ({env}):[/] {(autoConnectEnabled ? "enabled" : "disabled")}{(autoConnectPref.HasValue ? "" : " (default)")}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]Preferences: {Markup.Escape(filePath)}[/]");
        AnsiConsole.WriteLine();

        return 0;
    }
}
