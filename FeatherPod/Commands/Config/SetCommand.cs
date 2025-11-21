using FeatherPod.Infrastructure;
using FeatherPod.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Config;

internal sealed class SetCommand : Command<ConfigSetSettings>
{
    public override int Execute(CommandContext context, ConfigSetSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Configuration[/]");
        AnsiConsole.WriteLine();

        // Handle normalization setting (global, not per-environment)
        if (settings.Key.Equals("normalization", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.Value))
            {
                AnsiConsole.MarkupLine("[red]Value required.[/] Use: [cyan]true[/] or [cyan]false[/]");

                return 1;
            }

            if (!bool.TryParse(settings.Value, out var enabled))
            {
                AnsiConsole.MarkupLine($"[red]Invalid value:[/] {Markup.Escape(settings.Value)}. Use: [cyan]true[/] or [cyan]false[/]");

                return 1;
            }

            PreferencesHelpers.SetNormalizationEnabled(enabled);

            var filePath = PreferencesHelpers.GetPreferencesPath();

            AnsiConsole.MarkupLine($"[green]✓[/] Audio normalization {(enabled ? "enabled" : "disabled")}");
            AnsiConsole.MarkupLine($"[grey]Saved to {filePath}[/]");

            return 0;
        }

        // Handle auto-connect setting (global, not per-environment)
        if (settings.Key.Equals("auto-connect", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.Value))
            {
                AnsiConsole.MarkupLine("[red]Value required.[/] Use: [cyan]true[/] or [cyan]false[/]");

                return 1;
            }

            if (!bool.TryParse(settings.Value, out var enabled))
            {
                AnsiConsole.MarkupLine($"[red]Invalid value:[/] {Markup.Escape(settings.Value)}. Use: [cyan]true[/] or [cyan]false[/]");

                return 1;
            }

            PreferencesHelpers.SetAutoConnectEnabled(enabled);

            var filePath = PreferencesHelpers.GetPreferencesPath();

            AnsiConsole.MarkupLine($"[green]✓[/] Auto-connect on startup {(enabled ? "enabled" : "disabled")}");
            AnsiConsole.MarkupLine($"[grey]Saved to {filePath}[/]");

            return 0;
        }

        // Handle api-key setting (per-environment)
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        if (!settings.Key.Equals("api-key", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]Unknown configuration key:[/] {Markup.Escape(settings.Key)}");
            AnsiConsole.MarkupLine("Available keys: [cyan]api-key[/], [cyan]normalization[/], [cyan]auto-connect[/]");

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Value))
        {
            AnsiConsole.MarkupLine("[red]API key cannot be empty.[/]");

            return 1;
        }

        PreferencesHelpers.SaveApiKey(env, settings.Value);

        var prefsPath = PreferencesHelpers.GetPreferencesPath();

        AnsiConsole.MarkupLine($"[green]✓[/] API key saved to [cyan]{prefsPath}[/]");

        return 0;
    }
}
