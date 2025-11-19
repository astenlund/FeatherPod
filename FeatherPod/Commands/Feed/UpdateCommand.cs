using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Feed;

internal sealed class UpdateCommand : AsyncCommand<UpdateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UpdateSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Feed Management - Update Feed[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Get feed ID
        var feedId = settings.FeedId?.Trim();
        if (string.IsNullOrWhiteSpace(feedId))
        {
            var feed = await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
            if (feed == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No feeds available.");
                return 1;
            }
            feedId = feed.Id;
        }

        // Get current feed config
        var currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, feedId);
        if (currentFeed == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Feed '{feedId}' not found.");
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
        if (settings.Email != null)
            updateFields["email"] = settings.Email.Trim();
        if (!string.IsNullOrWhiteSpace(settings.Language))
            updateFields["language"] = settings.Language.Trim();
        if (settings.Category != null)
            updateFields["category"] = settings.Category.Trim();

        if (updateFields.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No fields to update.[/] Use options like --title, --author, --description to specify fields.");
            return 1;
        }

        try
        {
            var json = JsonSerializer.Serialize(updateFields);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync($"/api/feeds/{feedId}", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Updated feed: [cyan]{Markup.Escape(feedId)}[/]");
                AnsiConsole.WriteLine();

                // Show updated fields
                foreach (var field in updateFields)
                {
                    AnsiConsole.MarkupLine($"  {field.Key}: [cyan]{Markup.Escape(field.Value.ToString() ?? "")}[/]");
                }
                AnsiConsole.WriteLine();

                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to update feed: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error updating feed: {ex.Message}");
            return 1;
        }
    }
}
