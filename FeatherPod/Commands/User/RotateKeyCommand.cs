using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.User;

internal sealed class RotateKeyCommand : AsyncCommand<RotateKeySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RotateKeySettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod User Management - Rotate API Key[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        var userId = settings.UserId.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] User ID cannot be empty");
            return 1;
        }

        // Confirm rotation
        var confirm = AnsiConsole.Confirm($"Are you sure you want to regenerate the API key for user [cyan]{Markup.Escape(userId)}[/]? The old key will stop working.", false);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 0;
        }

        try
        {
            var response = await httpClient.PostAsync($"/api/users/{Uri.EscapeDataString(userId)}/key/regenerate", null);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                var responseData = JsonSerializer.Deserialize<JsonElement>(responseJson);

                AnsiConsole.MarkupLine($"[green]✓[/] API key regenerated successfully");
                AnsiConsole.WriteLine();

                // Display new API key (only shown once!)
                if (responseData.TryGetProperty("apiKey", out var apiKeyElement))
                {
                    var apiKey = apiKeyElement.GetString();
                    AnsiConsole.MarkupLine("[yellow bold]⚠ New API Key (save this now - it will NOT be shown again!):[/]");
                    AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(apiKey ?? "")}[/]");
                    AnsiConsole.WriteLine();
                }

                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to regenerate API key: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error regenerating API key: {ex.Message}");
            return 1;
        }
    }
}
