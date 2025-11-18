using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.User;

internal sealed class ListSettings : CommandSettings
{
    [CommandOption("--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
