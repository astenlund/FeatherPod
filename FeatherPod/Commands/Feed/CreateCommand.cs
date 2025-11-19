using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Server.Models;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Feed;

internal sealed class CreateCommand : AsyncCommand<CreateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CreateSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Feed Management - Create Feed[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Get or prompt for required fields
        var id = settings.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = AnsiConsole.Ask<string>("Feed [cyan]ID[/] (URL-friendly slug):");
        }

        var title = settings.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = AnsiConsole.Ask<string>("Feed [cyan]title[/]:");
        }

        var author = settings.Author;
        if (string.IsNullOrWhiteSpace(author))
        {
            author = AnsiConsole.Ask<string>("Feed [cyan]author[/]:");
        }

        // Optional fields
        var description = settings.Description ?? AnsiConsole.Ask("Description (optional):", string.Empty);
        var summary = settings.Summary ?? AnsiConsole.Ask("Summary (optional, defaults to description):", string.Empty);
        var email = settings.Email ?? AnsiConsole.Ask("Email (optional):", string.Empty);
        var language = settings.Language ?? AnsiConsole.Ask("Language:", "en");
        var category = settings.Category ?? AnsiConsole.Ask("Category (optional):", string.Empty);

        var feedConfig = new FeedConfig
        {
            Id = id.Trim(),
            Title = title.Trim(),
            Description = string.IsNullOrEmpty(description) ? null : description.Trim(),
            Summary = string.IsNullOrEmpty(summary) ? null : summary.Trim(),
            Author = author.Trim(),
            Email = string.IsNullOrEmpty(email) ? null : email.Trim(),
            Language = language.Trim(),
            Category = string.IsNullOrEmpty(category) ? null : category.Trim()
        };

        try
        {
            var json = JsonSerializer.Serialize(feedConfig);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("/api/feeds", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Created feed: [cyan]{Markup.Escape(feedConfig.Id)}[/]");

                // Upload icon if provided
                var iconPath = settings.IconPath?.Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(iconPath))
                {
                    if (!File.Exists(iconPath))
                    {
                        AnsiConsole.MarkupLine($"[yellow]⚠[/] Icon file not found: {Markup.Escape(iconPath)}");
                    }
                    else
                    {
                        await FeedHelpers.UploadIconAsync(httpClient, feedConfig.Id, iconPath);
                    }
                }

                AnsiConsole.WriteLine();
                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to create feed: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error creating feed: {ex.Message}");
            return 1;
        }
    }
}
