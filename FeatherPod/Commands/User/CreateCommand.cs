using System.Text;
using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.User;

internal sealed class CreateCommand : AsyncCommand<CreateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CreateSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod User Management - Create User[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        var userId = settings.UserId.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            Out.Error("User ID cannot be empty");
            return 1;
        }

        var name = settings.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = AnsiConsole.Ask<string>("User's [bold]display name[/]:");
        }

        var email = settings.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = AnsiConsole.Prompt(
                new TextPrompt<string>("User's [bold]email address[/] (optional):")
                    .AllowEmpty());
        }
        if (string.IsNullOrWhiteSpace(email)) email = null;

        var role = settings.Role;
        if (string.IsNullOrWhiteSpace(role))
        {
            role = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select [bold]user role[/]:")
                    .AddChoices("Admin", "FeedOwner"));
        }

        if (role != "Admin" && role != "FeedOwner")
        {
            Out.Error("Role must be either 'Admin' or 'FeedOwner'");

            return 1;
        }

        var ownedFeeds = new List<string>();
        if (role == "FeedOwner")
        {
            if (!string.IsNullOrWhiteSpace(settings.OwnedFeeds))
            {
                ownedFeeds = settings.OwnedFeeds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else
            {
                var feedsInput = AnsiConsole.Ask("Enter [bold]feed IDs[/] to own (comma-separated, or leave empty):", string.Empty);
                if (!string.IsNullOrWhiteSpace(feedsInput))
                {
                    ownedFeeds = feedsInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }
            }
        }

        var requestBody = new
        {
            id = userId,
            name,
            email,
            role,
            ownedFeeds
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await httpClient.PostAsync("/api/users", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                var responseData = JsonSerializer.Deserialize<JsonElement>(responseJson);

                Out.Success("User created successfully");
                Out.BlankLine();

                if (responseData.TryGetProperty("apiKey", out var apiKeyElement))
                {
                    var apiKey = apiKeyElement.GetString();
                    Out.MarkupLine($"[yellow bold]API Key (save this now, it will NOT be shown again):[/] [cyan]{Markup.Escape(apiKey ?? "")}[/]");
                    Out.BlankLine();
                }

                return 0;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Out.Error($"Failed to create user: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                try
                {
                    var errorData = JsonSerializer.Deserialize<JsonElement>(errorContent);
                    if (errorData.TryGetProperty("error", out var errorMsg))
                    {
                        Out.Error(Markup.Escape(errorMsg.GetString() ?? ""));
                    }
                }
                catch
                {
                    Out.Error(Markup.Escape(errorContent));
                }
            }

            return 1;
        }
        catch (Exception ex)
        {
            Out.Error($"Error creating user: {ex.Message}");

            return 1;
        }
    }
}
