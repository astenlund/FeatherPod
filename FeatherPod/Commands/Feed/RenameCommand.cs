using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Feed;

internal sealed class RenameCommand : AsyncCommand<RenameSettings>
{
    /// <summary>
    /// Core rename operation - can be called from CLI or InteractiveCommand.
    /// </summary>
    public static async Task<FeedOperationResult> RenameFeedAsync(
        HttpClient httpClient,
        string oldId,
        string newId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsync($"/api/feeds/{oldId}/rename?newId={Uri.EscapeDataString(newId)}", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Renamed feed from [cyan]{Markup.Escape(oldId)}[/] to [cyan]{Markup.Escape(newId)}[/]");

                return new() { Success = true, FeedId = newId, OldFeedId = oldId };
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            AnsiConsole.MarkupLine($"[red]✗[/] Failed to rename feed: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Error: {Markup.Escape(errorContent)}");
            }

            return new() { Success = false, ErrorMessage = errorContent };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error renaming feed: {ex.Message}");

            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RenameSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Feed Management - Rename Feed[/]");
        AnsiConsole.WriteLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Get current feed ID
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

        // Verify feed exists
        var currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, feedId);
        if (currentFeed == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Feed '{feedId}' not found.");
            return 1;
        }

        // Get new ID
        var newId = settings.NewId?.Trim();
        if (string.IsNullOrWhiteSpace(newId))
        {
            newId = AnsiConsole.Ask<string>("New feed [cyan]ID[/]:");
        }

        var result = await RenameFeedAsync(httpClient, feedId, newId, cancellationToken);

        if (result.Success)
        {
            AnsiConsole.WriteLine();
        }

        return result.Success ? 0 : 1;
    }
}
