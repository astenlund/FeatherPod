using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.User;

internal sealed class RotateKeySettings : CommandSettings
{
    [CommandArgument(0, "[user-id]")]
    [Description("User ID to regenerate API key for (defaults to current user)")]
    public string? UserId { get; init; }

    [CommandOption("-e|--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
