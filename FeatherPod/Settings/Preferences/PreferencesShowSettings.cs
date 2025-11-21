using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Preferences;

internal sealed class PreferencesShowSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
