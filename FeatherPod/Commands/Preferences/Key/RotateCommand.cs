using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.Preferences;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Preferences.Key;

internal sealed class RotateCommand : AsyncCommand<KeyShowSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, KeyShowSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Preferences - Rotate API Key[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, userInfo) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        if (userInfo == null)
        {
            Out.Error("Could not determine current user.");
            return 1;
        }

        Out.MarkupLine($"Rotating API key for: [cyan]{Markup.Escape(userInfo.Id)}[/]");
        Out.BlankLine();

        var confirm = await AnsiConsole.ConfirmAsync(
            "Are you sure you want to rotate your API key? The current key will stop working.",
            false, cancellationToken);
        if (!confirm)
        {
            Out.Cancelled();
            return 0;
        }

        try
        {
            var response = await httpClient.PostAsync(
                $"/api/users/{Uri.EscapeDataString(userInfo.Id)}/key/regenerate", null, cancellationToken);

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
                PreferencesHelpers.SaveApiKey(env, apiKey);

                Out.Success("API key rotated and saved to preferences");
                Out.BlankLine();
                Out.MarkupLine($"[yellow bold]New API Key:[/] [cyan]{Markup.Escape(apiKey)}[/]");
                Out.BlankLine();

                return 0;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Out.Error($"Failed to rotate API key: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                Out.Error(Markup.Escape(errorContent));
            }

            return 1;
        }
        catch (Exception ex)
        {
            Out.Error($"Error rotating API key: {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
