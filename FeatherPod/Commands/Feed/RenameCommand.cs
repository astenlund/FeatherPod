using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Feed;

internal sealed class RenameCommand : AsyncCommand<RenameSettings>
{
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

        try
        {
            var response = await httpClient.PostAsync($"/api/feeds/{feedId}/rename?newId={Uri.EscapeDataString(newId)}", null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Renamed feed from [cyan]{Markup.Escape(feedId)}[/] to [cyan]{Markup.Escape(newId)}[/]");
                AnsiConsole.WriteLine();
                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to rename feed: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error renaming feed: {ex.Message}");
            return 1;
        }
    }
}
