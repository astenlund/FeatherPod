using FeatherPod.Infrastructure;
using FeatherPod.Settings.Admin;
using FeatherPod.Shared.Services;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Admin;

internal sealed class SeedCommand : AsyncCommand<SeedSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SeedSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Admin Seed[/]");
        Out.BlankLine();

        var userId = string.IsNullOrWhiteSpace(settings.UserId) ? DevSeedService.DefaultUserId : settings.UserId.Trim();
        var name = string.IsNullOrWhiteSpace(settings.Name) ? DevSeedService.DefaultName : settings.Name.Trim();
        var email = string.IsNullOrWhiteSpace(settings.Email) ? null : settings.Email.Trim();
        var connectionString = string.IsNullOrWhiteSpace(settings.ConnectionString) ? DevSeedService.DefaultConnectionString : settings.ConnectionString.Trim();
        var containerName = string.IsNullOrWhiteSpace(settings.Container) ? DevSeedService.DefaultContainer : settings.Container.Trim();

        try
        {
            var serviceClient = StorageClientFactory.CreateBlobServiceClient(connectionString, null);
            var container = serviceClient.GetBlobContainerClient(containerName);

            var result = await DevSeedService.SeedAdminAsync(container, userId, name, email, cancellationToken);

            if (result.Outcome == SeedOutcome.UserIdTaken)
            {
                Out.Warning($"A user with ID [cyan]{Markup.Escape(userId)}[/] already exists. No changes made.");
                Out.MarkupLine("[grey]Delete the user or rotate its key instead of re-seeding.[/]");
                Out.BlankLine().Flush();

                return 1;
            }

            Out.Success($"Seeded admin [cyan]{Markup.Escape(userId)}[/] into container [cyan]{Markup.Escape(containerName)}[/]");
            Out.BlankLine();
            Out.MarkupLine($"[yellow bold]API Key (save this now, it will NOT be shown again):[/] [cyan]{Markup.Escape(result.ApiKey ?? "")}[/]");
            Out.BlankLine();
            Out.MarkupLine($"[grey]Save it with:[/] [cyan]featherpod preferences key set {Markup.Escape(result.ApiKey ?? "")} -e Dev[/]");
            Out.BlankLine().Flush();

            return 0;
        }
        catch (Exception ex)
        {
            Out.Error($"Failed to seed admin user: {Markup.Escape(ex.Message)}");
            Out.MarkupLine("[grey]Is Azurite (or the target storage account) running and reachable?[/]");
            Out.BlankLine().Flush();

            return 1;
        }
    }
}
