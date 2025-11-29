using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.User;

internal sealed class RotateKeyCommand : AsyncCommand<RotateKeySettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RotateKeySettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod User Management - Rotate API Key[/]");
        Out.BlankLine();

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
                    Out.Error($"Failed to get current user: {meResponse.StatusCode}");

                    return 1;
                }

                var meJson = await meResponse.Content.ReadAsStringAsync(cancellationToken);
                var meData = JsonSerializer.Deserialize<JsonElement>(meJson);

                if (!meData.TryGetProperty("id", out var idElement))
                {
                    Out.Error("Could not determine current user ID");

                    return 1;
                }

                userId = idElement.GetString();

                Out.MarkupLine($"[grey]Rotating API key for current user:[/] [cyan]{Markup.Escape(userId ?? "")}[/]");
                Out.BlankLine();
            }
            catch (Exception ex)
            {
                Out.Error($"Failed to get current user: {ex.Message}");

                return 1;
            }
        }

        var confirm = await AnsiConsole.ConfirmAsync($"Are you sure you want to regenerate the API key for user [cyan]{Markup.Escape(userId ?? "")}[/]?" +
                                                     " The old key will stop working.", false, cancellationToken);
        if (!confirm)
        {
            Out.Cancelled();

            return 0;
        }

        try
        {
            var response = await httpClient.PostAsync($"/api/users/{Uri.EscapeDataString(userId!)}/key/regenerate", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var responseData = JsonSerializer.Deserialize<JsonElement>(responseJson);

                Out.Success("API key regenerated successfully");
                Out.BlankLine();

                if (responseData.TryGetProperty("apiKey", out var apiKeyElement))
                {
                    var apiKey = apiKeyElement.GetString();

                    Out.MarkupLine($"[yellow bold]New API Key:[/] [cyan]{Markup.Escape(apiKey ?? "")}[/]");
                    Out.BlankLine();

                    // Prompt to save the new key
                    var saveKey = await AnsiConsole.ConfirmAsync($"Save this key to preferences for {env}?", true, cancellationToken);
                    if (saveKey && !string.IsNullOrEmpty(apiKey))
                    {
                        PreferencesHelpers.SaveApiKey(env, apiKey);
                        var prefsPath = PreferencesHelpers.GetPreferencesPath();
                        Out.Success($"API key saved to [grey]{Markup.Escape(prefsPath)}[/]");
                    }
                    else
                    {
                        Out.Warning("API key was NOT saved. Copy it now - it will NOT be shown again!");
                    }

                    Out.BlankLine();
                }

                return 0;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Out.Error($"Failed to regenerate API key: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                Out.Error(Markup.Escape(errorContent));
            }

            return 1;
        }
        catch (Exception ex)
        {
            Out.Error($"Error regenerating API key: {ex.Message}");

            return 1;
        }
    }
}
