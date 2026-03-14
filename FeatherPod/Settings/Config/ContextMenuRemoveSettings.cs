using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Config;

internal sealed class ContextMenuRemoveSettings : CommandSettings
{
    [CommandOption("--all")]
    [Description("Remove all context menu entries")]
    public bool All { get; init; }

    [CommandOption("-f|--feed")]
    [Description("Feed ID to remove")]
    public string? FeedId { get; init; }
}
