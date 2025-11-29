using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed;

internal sealed class UnsetIconCommand : AsyncCommand<UnsetIconSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UnsetIconSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Icon Removal[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null)
        {
            return 1;
        }

        // Select feed (use argument if provided, otherwise prompt user to select)
        var feed = !string.IsNullOrEmpty(settings.FeedId)
            ? await FeedHelpers.GetFeedByIdAsync(httpClient, settings.FeedId)
            : await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);

        if (feed == null)
        {
            Out.Error(!string.IsNullOrEmpty(settings.FeedId)
                ? $"Feed '{settings.FeedId}' not found."
                : "No feeds available. Create a feed first.");

            return 1;
        }

        // Confirm deletion
        var confirmed = new MenuBuilder<bool?>()
            .WithTitle($"Remove icon from feed [cyan]{Markup.Escape(feed.Title)}[/]?")
            .WithHint("(arrow keys or Y/N, Esc to cancel)")
            .AddOption("Y", "Yes", true)
            .AddOption("N", "No", false)
            .AllowCancel(true, false)
            .Show();

        if (confirmed != true)
        {
            Out.Cancelled();

            return 1;
        }

        Out.BlankLine();

        // Delete icon
        var success = await FeedHelpers.DeleteIconAsync(httpClient, feed.Id);

        Out.BlankLine().Flush();

        return success ? 0 : 1;
    }
}
