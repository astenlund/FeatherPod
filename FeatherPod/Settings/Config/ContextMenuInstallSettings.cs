using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Config;

internal sealed class ContextMenuInstallSettings : CommandSettings
{
    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string Environment { get; init; } = "Prod";

    [CommandOption("-f|--feed")]
    [Description("Feed ID to register")]
    public string? FeedId { get; init; }

    [CommandOption("--delete-after")]
    [Description("Delete source files after successful upload")]
    public bool DeleteAfter { get; init; }
}
