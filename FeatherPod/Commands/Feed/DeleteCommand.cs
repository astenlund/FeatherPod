using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed;

internal sealed class DeleteCommand : AsyncCommand<DeleteSettings>
{
    /// <summary>
    /// Core delete operation - can be called from CLI or InteractiveCommand.
    /// </summary>
    public static async Task<FeedOperationResult> DeleteFeedAsync(
        HttpClient httpClient,
        string feedId,
        bool skipConfirmation = false,
        CancellationToken cancellationToken = default)
    {
        // Confirm deletion unless skipped
        if (!skipConfirmation)
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
                Out.Cancelled();

                return new() { Success = false };
            }
        }

        try
        {
            var response = await httpClient.DeleteAsync($"/api/feeds/{feedId}", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                Out.Success($"Deleted feed: [cyan]{Markup.Escape(feedId)}[/]");

                return new() { Success = true, FeedId = feedId };
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            Out.Error($"Failed to delete feed: {response.StatusCode}");

            if (!string.IsNullOrEmpty(errorContent))
            {
                Out.Error(Markup.Escape(errorContent));
            }

            return new() { Success = false, ErrorMessage = errorContent };
        }
        catch (Exception ex)
        {
            Out.Error($"Error deleting feed: {ex.Message}");

            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, DeleteSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Feed Management - Delete Feed[/]");
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

        // Verify feed exists
        var currentFeed = await FeedHelpers.GetFeedByIdAsync(httpClient, feedId);
        if (currentFeed == null)
        {
            Out.Error($"Feed '{feedId}' not found.");
            return 1;
        }

        var result = await DeleteFeedAsync(httpClient, feedId, settings.Force, cancellationToken);

        if (result.Success)
        {
            Out.BlankLine();
        }

        return result.Success ? 0 : 1;
    }
}
