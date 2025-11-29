using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed;

internal sealed class CreateCommand : AsyncCommand<CreateSettings>
{
    /// <summary>
    /// Core create operation - can be called from CLI or InteractiveCommand.
    /// </summary>
    public static async Task<FeedOperationResult> CreateFeedAsync(
        HttpClient httpClient,
        FeedConfig feedConfig,
        string? iconPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(feedConfig);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("/api/feeds", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                Out.Success($"Created feed: [cyan]{Markup.Escape(feedConfig.Id)}[/]");

                // Upload icon if provided
                if (!string.IsNullOrEmpty(iconPath))
                {
                    if (!File.Exists(iconPath))
                    {
                        Out.Error($"Icon file not found: {Markup.Escape(iconPath)}");
                    }
                    else
                    {
                        await FeedHelpers.UploadIconAsync(httpClient, feedConfig.Id, iconPath);
                    }
                }

                return new() { Success = true, FeedId = feedConfig.Id, Feed = feedConfig };
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Out.Error($"Failed to create feed: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                Out.Error(Markup.Escape(errorContent));
            }

            return new() { Success = false, ErrorMessage = errorContent };
        }
        catch (Exception ex)
        {
            Out.Error($"Error creating feed: {ex.Message}");

            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, CreateSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Feed Management - Create Feed[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Get or prompt for required fields
        var id = settings.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = AnsiConsole.Ask<string>("Feed [bold]ID[/] (URL-friendly slug):");
        }

        var title = settings.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = AnsiConsole.Ask<string>("Feed [bold]title[/]:");
        }

        var author = settings.Author;
        if (string.IsNullOrWhiteSpace(author))
        {
            author = AnsiConsole.Ask<string>("Feed [bold]author[/]:");
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

        var iconPath = settings.IconPath?.Trim().Trim('"', '\'');
        var result = await CreateFeedAsync(httpClient, feedConfig, iconPath, cancellationToken);

        if (result.Success)
        {
            Out.BlankLine();
        }

        return result.Success ? 0 : 1;
    }
}
