using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.YouTube;

internal sealed class SetCookiesSettings : CommandSettings
{
    [CommandArgument(0, "<cookie-file-path>")]
    [Description("Path to Netscape-format cookies.txt file")]
    public string CookieFilePath { get; init; } = string.Empty;

    [CommandOption("-e|--environment <ENV>")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
