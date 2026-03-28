using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.YouTube;

internal sealed class CookieStatusSettings : CommandSettings
{
    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
