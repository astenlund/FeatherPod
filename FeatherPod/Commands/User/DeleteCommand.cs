using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.User;

internal sealed class DeleteCommand : AsyncCommand<DeleteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeleteSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod User Management - Delete User[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null)
        {
            return 1;
        }

        var userId = settings.UserId.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] User ID cannot be empty");

            return 1;
        }

        var confirm = await AnsiConsole.ConfirmAsync($"Are you sure you want to delete user [cyan]{Markup.Escape(userId)}[/]?", false, cancellationToken);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");

            return 0;
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/users/{Uri.EscapeDataString(userId)}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[green]✓[/] User deleted successfully");
                AnsiConsole.WriteLine();

                return 0;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            AnsiConsole.MarkupLine($"[red]✗[/] Failed to delete user: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
            }

            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error deleting user: {ex.Message}");
            AnsiConsole.WriteLine();

            return 1;
        }
    }
}
