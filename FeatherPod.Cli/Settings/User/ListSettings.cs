using Spectre.Console.Cli;
using System.ComponentModel;

namespace FeatherPod.Cli.Settings.User;

internal sealed class ListSettings : CommandSettings
{
    [CommandOption("--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
