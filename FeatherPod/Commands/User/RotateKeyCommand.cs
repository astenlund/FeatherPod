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

        // Get user ID - either from argument or from /api/me
        var userId = settings.UserId?.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            // Fetch current user ID from server
            try
            {
                var meResponse = await httpClient.GetAsync("/api/users/me", cancellationToken);
                if (!meResponse.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] Failed to get current user: {meResponse.StatusCode}");

                    return 1;
                }

                var meJson = await meResponse.Content.ReadAsStringAsync(cancellationToken);
                var meData = JsonSerializer.Deserialize<JsonElement>(meJson);

                if (!meData.TryGetProperty("id", out var idElement))
                {
                    AnsiConsole.MarkupLine("[red]✗[/] Could not determine current user ID");

                    return 1;
                }

                userId = idElement.GetString();

                AnsiConsole.MarkupLine($"[grey]Rotating API key for current user:[/] [cyan]{Markup.Escape(userId ?? "")}[/]");
                AnsiConsole.WriteLine();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to get current user: {ex.Message}");

                return 1;
            }
        }

        var confirm = await AnsiConsole.ConfirmAsync($"Are you sure you want to regenerate the API key for user [cyan]{Markup.Escape(userId ?? "")}[/]?" +
                                                     " The old key will stop working.", false, cancellationToken);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");

            return 0;
        }

        try
        {
            var response = await httpClient.PostAsync($"/api/users/{Uri.EscapeDataString(userId!)}/key/regenerate", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var responseData = JsonSerializer.Deserialize<JsonElement>(responseJson);

                AnsiConsole.MarkupLine($"[green]✓[/] API key regenerated successfully");
                AnsiConsole.WriteLine();

                if (responseData.TryGetProperty("apiKey", out var apiKeyElement))
                {
                    var apiKey = apiKeyElement.GetString();

                    AnsiConsole.MarkupLine($"[yellow bold]New API Key:[/] [cyan]{Markup.Escape(apiKey ?? "")}[/]");
                    AnsiConsole.WriteLine();

                    // Prompt to save the new key
                    var saveKey = await AnsiConsole.ConfirmAsync($"Save this key to preferences for {env}?", true, cancellationToken);
                    if (saveKey && !string.IsNullOrEmpty(apiKey))
                    {
                        PreferencesHelpers.SaveApiKey(env, apiKey);
                        var prefsPath = PreferencesHelpers.GetPreferencesPath();
                        AnsiConsole.MarkupLine($"[green]✓[/] API key saved to [grey]{Markup.Escape(prefsPath)}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]Δ[/] API key was NOT saved. Copy it now - it will NOT be shown again!");
                    }

                    AnsiConsole.WriteLine();
                }

                return 0;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            AnsiConsole.MarkupLine($"[red]✗[/] Failed to regenerate API key: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(errorContent)}");
            }

            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error regenerating API key: {ex.Message}");

            return 1;
        }
    }
}
