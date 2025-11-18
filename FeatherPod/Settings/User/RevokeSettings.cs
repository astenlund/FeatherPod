using Spectre.Console.Cli;
using System.ComponentModel;

namespace FeatherPod.Cli.Settings.User;

internal sealed class RevokeSettings : CommandSettings
{
    [CommandArgument(0, "<user-id>")]
    [Description("User ID to revoke feed ownership from")]
    public string UserId { get; init; } = string.Empty;

    [CommandArgument(1, "<feed-id>")]
    [Description("Feed ID to revoke ownership of")]
    public string FeedId { get; init; } = string.Empty;

    [CommandOption("--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
