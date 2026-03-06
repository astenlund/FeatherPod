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

        var userId = settings.UserId.Trim();

        var confirm = await AnsiConsole.ConfirmAsync(
            $"Are you sure you want to regenerate the API key for user '{userId}'?" +
            " The old key will stop working.", false, cancellationToken);
        if (!confirm)
        {
            Out.Cancelled();

            return 0;
        }

        try
        {
            var response = await httpClient.PostAsync($"/api/users/{Uri.EscapeDataString(userId)}/key/regenerate", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var responseData = JsonSerializer.Deserialize<JsonElement>(responseJson);

                if (!responseData.TryGetProperty("apiKey", out var apiKeyElement) ||
                    string.IsNullOrEmpty(apiKeyElement.GetString()))
                {
                    Out.Error("Key was rotated on the server but no new key was returned. Contact an admin.");
                    return 1;
                }

                var apiKey = apiKeyElement.GetString()!;

                Out.Success("API key regenerated successfully");
                Out.BlankLine();
                Out.MarkupLine($"[yellow bold]New API Key:[/] [cyan]{Markup.Escape(apiKey)}[/]");
                Out.BlankLine();
                Out.Warning("Copy this key now - it will NOT be shown again!");
                Out.BlankLine();

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
            Out.Error($"Error regenerating API key: {Markup.Escape(ex.Message)}");

            return 1;
        }
    }
}
