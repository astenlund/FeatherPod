using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands.User;

internal sealed class RevokeCommand : AsyncCommand<RevokeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RevokeSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod User Management - Revoke Feed Ownership[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        var userId = settings.UserId.Trim();
        var feedId = settings.FeedId.Trim();

        if (string.IsNullOrWhiteSpace(userId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] User ID cannot be empty");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(feedId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Feed ID cannot be empty");
            return 1;
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/users/{Uri.EscapeDataString(userId)}/feeds/{Uri.EscapeDataString(feedId)}");

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Revoked feed [cyan]{Markup.Escape(feedId)}[/] ownership from user [cyan]{Markup.Escape(userId)}[/]");
                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to revoke feed ownership: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error revoking feed ownership: {ex.Message}");
            return 1;
        }
    }
}
