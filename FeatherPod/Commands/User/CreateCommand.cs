using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings.User;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;
using System.Text.Json;

namespace FeatherPod.Cli.Commands.User;

internal sealed class CreateCommand : AsyncCommand<CreateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CreateSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod User Management - Create User[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Validate user ID
        var userId = settings.UserId.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] User ID cannot be empty");
            return 1;
        }

        // Get or prompt for user details
        var name = settings.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = AnsiConsole.Ask<string>("User's [cyan]display name[/]:");
        }

        var email = settings.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = AnsiConsole.Ask<string>("User's [cyan]email address[/]:");
        }

        // Get or prompt for role
        var role = settings.Role;
        if (string.IsNullOrWhiteSpace(role))
        {
            role = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select [cyan]user role[/]:")
                    .AddChoices("Admin", "FeedOwner"));
        }

        // Validate role
        if (role != "Admin" && role != "FeedOwner")
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Role must be either 'Admin' or 'FeedOwner'");
            return 1;
        }

        // Get owned feeds if FeedOwner
        var ownedFeeds = new List<string>();
        if (role == "FeedOwner")
        {
            if (!string.IsNullOrWhiteSpace(settings.OwnedFeeds))
            {
                ownedFeeds = settings.OwnedFeeds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else
            {
                // Prompt for feeds
                var feedsInput = AnsiConsole.Ask<string>("Enter [cyan]feed IDs[/] to own (comma-separated, or leave empty):", string.Empty);
                if (!string.IsNullOrWhiteSpace(feedsInput))
                {
                    ownedFeeds = feedsInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }
            }
        }

        // Create request body
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
            var response = await httpClient.PostAsync("/api/users", content);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync();
                var responseData = JsonSerializer.Deserialize<JsonElement>(responseJson);

                AnsiConsole.MarkupLine($"[green]✓[/] User created successfully");
                AnsiConsole.WriteLine();

                // Display API key (only shown once!)
                if (responseData.TryGetProperty("apiKey", out var apiKeyElement))
                {
                    var apiKey = apiKeyElement.GetString();
                    AnsiConsole.MarkupLine("[yellow bold]⚠ API Key (save this now - it will NOT be shown again!):[/]");
                    AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(apiKey ?? "")}[/]");
                    AnsiConsole.WriteLine();
                }

                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to create user: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    try
                    {
                        var errorData = JsonSerializer.Deserialize<JsonElement>(errorContent);
                        if (errorData.TryGetProperty("error", out var errorMsg))
                        {
                            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorMsg.GetString() ?? "")}");
                        }
                    }
                    catch
                    {
                        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                    }
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error creating user: {ex.Message}");
            return 1;
        }
    }
}
