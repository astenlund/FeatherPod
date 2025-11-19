using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Feed;

internal sealed class DeleteCommand : AsyncCommand<DeleteSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DeleteSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Feed Management - Delete Feed[/]");
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

        // Verify feed exists
        var currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, feedId);
        if (currentFeed == null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Feed '{feedId}' not found.");
            return 1;
        }

        // Confirm deletion unless --force is used
        if (!settings.Force)
        {
            var confirmed = new MenuBuilder<bool?>()
                .WithTitle($"[red]Delete feed[/] [cyan]{Markup.Escape(feedId)}[/] and all its episodes?")
                .WithHint("(arrow keys or Y/N, Esc to cancel)")
                .AddOption("Y", "Yes", true)
                .AddOption("N", "No", false)
                .AllowCancel(true, false)
                .Show();

            if (confirmed != true)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 1;
            }
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/feeds/{feedId}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Deleted feed: [cyan]{Markup.Escape(feedId)}[/]");
                AnsiConsole.WriteLine();
                return 0;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to delete feed: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(errorContent)}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Error deleting feed: {ex.Message}");
            return 1;
        }
    }
}
