using System.Text;
using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.User;

internal sealed class GrantCommand : AsyncCommand<GrantSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GrantSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod User Management - Grant Feed Ownership[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
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

        var requestBody = new { feedId };
        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await httpClient.PostAsync($"/api/users/{Uri.EscapeDataString(userId)}/feeds", content);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Granted feed [cyan]{Markup.Escape(feedId)}[/] ownership to user [cyan]{Markup.Escape(userId)}[/]");
                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to grant feed ownership: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error granting feed ownership: {ex.Message}");
            return 1;
        }
    }
}
