using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Config;

internal sealed class ContextMenuInstallSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string Environment { get; init; } = "Prod";

    [CommandOption("-f|--feed")]
    [Description("Feed ID to register (optional, will prompt if not specified)")]
    public string? FeedId { get; init; }
}
