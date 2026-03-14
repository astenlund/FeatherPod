using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using FeatherPod.Shared.Models;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed;

internal sealed class UpdateCommand : AsyncCommand<UpdateSettings>
{
    /// <summary>
    /// Core update operation with interactive prompts - can be called from CLI or InteractiveCommand.
    /// </summary>
    public static async Task<FeedOperationResult> UpdateFeedInteractiveAsync(
        HttpClient httpClient,
        FeedConfig currentFeed,
        CancellationToken cancellationToken = default)
    {
        Out.MarkupLine($"Editing feed: [cyan]{Markup.Escape(currentFeed.Title)}[/]");
        Out.BlankLine();

        // Let user select which fields to edit
        var fieldOptions = new List<string>
        {
            $"Title: {currentFeed.Title}",
            $"Author: {currentFeed.Author}",
            $"Description: {currentFeed.Description ?? "(empty)"}",
            $"Summary: {currentFeed.Summary ?? "(empty)"}",
            $"Email: {currentFeed.Email ?? "(empty)"}",
            $"Language: {currentFeed.Language}",
            $"Category: {currentFeed.Category ?? "(empty)"}"
        };

        var selectedFields = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Select fields to edit:")
                .PageSize(10)
                .NotRequired()
                .InstructionsText("[grey]([blue]Space[/] to toggle, [green]Enter[/] to confirm, Enter with none to cancel)[/]")
                .AddChoices(fieldOptions));

        if (selectedFields.Count == 0)
        {
            Out.MarkupLine("[grey]No fields selected.[/]");

            return new() { Success = true, FeedId = currentFeed.Id };
        }

        Out.BlankLine();

        var updateFields = new Dictionary<string, object>();

        // Prompt only for selected fields
        if (selectedFields.Any(f => f.StartsWith("Title:")))
        {
            var newTitle = AnsiConsole.Ask("Title:", currentFeed.Title);
            if (newTitle != currentFeed.Title)
                updateFields["title"] = newTitle.Trim();
        }

        if (selectedFields.Any(f => f.StartsWith("Author:")))
        {
            var newAuthor = AnsiConsole.Ask("Author:", currentFeed.Author);
            if (newAuthor != currentFeed.Author)
                updateFields["author"] = newAuthor.Trim();
        }

        if (selectedFields.Any(f => f.StartsWith("Description:")))
        {
            var newDescription = AnsiConsole.Ask("Description:", currentFeed.Description ?? "");
            if (newDescription != (currentFeed.Description ?? ""))
                updateFields["description"] = string.IsNullOrEmpty(newDescription) ? null! : newDescription.Trim();
        }

        if (selectedFields.Any(f => f.StartsWith("Summary:")))
        {
            var newSummary = AnsiConsole.Ask("Summary:", currentFeed.Summary ?? "");
            if (newSummary != (currentFeed.Summary ?? ""))
                updateFields["summary"] = string.IsNullOrEmpty(newSummary) ? null! : newSummary.Trim();
        }

        if (selectedFields.Any(f => f.StartsWith("Email:")))
        {
            var newEmail = AnsiConsole.Ask("Email:", currentFeed.Email ?? "");
            if (newEmail != (currentFeed.Email ?? ""))
                updateFields["email"] = string.IsNullOrEmpty(newEmail) ? null! : newEmail.Trim();
        }

        if (selectedFields.Any(f => f.StartsWith("Language:")))
        {
            var newLanguage = AnsiConsole.Ask("Language:", currentFeed.Language);
            if (newLanguage != currentFeed.Language)
                updateFields["language"] = newLanguage.Trim();
        }

        if (selectedFields.Any(f => f.StartsWith("Category:")))
        {
            var newCategory = AnsiConsole.Ask("Category:", currentFeed.Category ?? "");
            if (newCategory != (currentFeed.Category ?? ""))
                updateFields["category"] = string.IsNullOrEmpty(newCategory) ? null! : newCategory.Trim();
        }

        if (updateFields.Count == 0)
        {
            Out.BlankLine();
            Out.MarkupLine("[grey]No changes made.[/]");

            return new() { Success = true, FeedId = currentFeed.Id };
        }

        try
        {
            var json = JsonSerializer.Serialize(updateFields);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync($"/api/feeds/{currentFeed.Id}", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                Out.Success($"Updated feed: [cyan]{Markup.Escape(currentFeed.Id)}[/]");
                Out.BlankLine();

                // Show updated fields
                foreach (var field in updateFields)
                {
                    Out.MarkupLine($"  {field.Key}: [cyan]{Markup.Escape(field.Value?.ToString() ?? "")}[/]");
                }

                return new() { Success = true, FeedId = currentFeed.Id };
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Out.Error($"Failed to update feed: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                Out.Error(Markup.Escape(errorContent));
            }

            return new() { Success = false, ErrorMessage = errorContent };
        }
        catch (Exception ex)
        {
            Out.Error($"Error updating feed: {ex.Message}");

            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, UpdateSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Feed Management - Update Feed[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Get feed ID
        var feedId = settings.FeedId?.Trim();
        if (string.IsNullOrWhiteSpace(feedId))
        {
            var feed = await FeedHelpers.SelectFeedAsync(httpClient);
            if (feed == null)
            {
                Out.Error("No feeds available.");
                return 1;
            }
            feedId = feed.Id;
        }

        // Get current feed config
        var currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, feedId);
        if (currentFeed == null)
        {
            Out.Error($"Feed '{feedId}' not found.");
            return 1;
        }

        // Build update object with only provided fields
        var updateFields = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(settings.Title))
            updateFields["title"] = settings.Title.Trim();
        if (!string.IsNullOrWhiteSpace(settings.Author))
            updateFields["author"] = settings.Author.Trim();
        if (settings.Description != null)
            updateFields["description"] = settings.Description.Trim();
        if (settings.Summary != null)
            updateFields["summary"] = settings.Summary.Trim();
        if (settings.Email != null)
            updateFields["email"] = settings.Email.Trim();
        if (!string.IsNullOrWhiteSpace(settings.Language))
            updateFields["language"] = settings.Language.Trim();
        if (settings.Category != null)
            updateFields["category"] = settings.Category.Trim();

        // Interactive mode if no fields specified
        if (updateFields.Count == 0)
        {
            var result = await UpdateFeedInteractiveAsync(httpClient, currentFeed, cancellationToken);

            if (result.Success)
            {
                Out.BlankLine();
            }

            return result.Success ? 0 : 1;
        }

        try
        {
            var json = JsonSerializer.Serialize(updateFields);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync($"/api/feeds/{feedId}", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                Out.Success($"Updated feed: [cyan]{Markup.Escape(feedId)}[/]");
                Out.BlankLine();

                // Show updated fields
                foreach (var field in updateFields)
                {
                    Out.MarkupLine($"  {field.Key}: [cyan]{Markup.Escape(field.Value.ToString() ?? "")}[/]");
                }
                Out.BlankLine().Flush();

                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Out.Error($"Failed to update feed: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    Out.Error(Markup.Escape(errorContent));
                }

                return 1;
            }
        }
        catch (Exception ex)
        {
            Out.Error($"Error updating feed: {ex.Message}");
            return 1;
        }
    }
}
