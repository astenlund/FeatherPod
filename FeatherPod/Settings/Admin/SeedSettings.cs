using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Admin;

internal sealed class SeedSettings : CommandSettings
{
    [CommandOption("-u|--user-id")]
    [Description("Admin user ID to create (default: admin)")]
    public string? UserId { get; init; }

    [CommandOption("-n|--name")]
    [Description("Admin display name (default: Admin)")]
    public string? Name { get; init; }

    [CommandOption("--email")]
    [Description("Admin email address (optional)")]
    public string? Email { get; init; }

    [CommandOption("--connection-string")]
    [Description("Azure Storage connection string (default: UseDevelopmentStorage=true for local Azurite)")]
    public string? ConnectionString { get; init; }

    [CommandOption("--container")]
    [Description("Blob container name (default: featherpod)")]
    public string? Container { get; init; }
}
