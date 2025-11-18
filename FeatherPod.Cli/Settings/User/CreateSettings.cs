using Spectre.Console.Cli;
using System.ComponentModel;

namespace FeatherPod.Cli.Settings.User;

internal sealed class CreateSettings : CommandSettings
{
    [CommandArgument(0, "<user-id>")]
    [Description("Unique user ID")]
    public string UserId { get; init; } = string.Empty;

    [CommandOption("-n|--name")]
    [Description("User's display name")]
    public string? Name { get; init; }

    [CommandOption("-e|--email")]
    [Description("User's email address")]
    public string? Email { get; init; }

    [CommandOption("-r|--role")]
    [Description("User role (Admin or FeedOwner)")]
    public string? Role { get; init; }

    [CommandOption("-f|--feeds")]
    [Description("Comma-separated list of feed IDs to own (for FeedOwner role)")]
    public string? OwnedFeeds { get; init; }

    [CommandOption("--environment")]
    [Description("Target environment (Dev, Test, Prod)")]
    public string? Environment { get; init; }
}
