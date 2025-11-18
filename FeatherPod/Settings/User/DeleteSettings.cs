using Spectre.Console.Cli;
using System.ComponentModel;

namespace FeatherPod.Cli.Settings.User;

internal sealed class DeleteSettings : CommandSettings
{
    [CommandArgument(0, "<user-id>")]
    [Description("User ID to delete")]
    public string UserId { get; init; } = string.Empty;

    [CommandOption("--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
