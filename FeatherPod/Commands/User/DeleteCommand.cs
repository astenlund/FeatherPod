using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands.User;

internal sealed class DeleteCommand : AsyncCommand<DeleteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeleteSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod User Management - Delete User[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        var userId = settings.UserId.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] User ID cannot be empty");
            return 1;
        }

        // Confirm deletion
        var confirm = AnsiConsole.Confirm($"Are you sure you want to delete user [cyan]{Markup.Escape(userId)}[/]?", false);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 0;
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/users/{Uri.EscapeDataString(userId)}");

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] User deleted successfully");
                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to delete user: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error deleting user: {ex.Message}");
            return 1;
        }
    }
}
