using FeatherPod.Infrastructure;
using FeatherPod.Settings.Icon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Icon;

internal sealed class UnsetCommand : AsyncCommand<UnsetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UnsetSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Icon Removal[/]");
        AnsiConsole.WriteLine();

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

        // Select feed (use -f flag if provided, otherwise prompt user to select)
        var feed = !string.IsNullOrEmpty(settings.FeedId)
            ? await FeedHelpers.GetFeedByIdAsync(httpClient, settings.FeedId)
            : await FeedHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);

        if (feed == null)
        {
            AnsiConsole.MarkupLine(!string.IsNullOrEmpty(settings.FeedId)
                ? $"[red]Error:[/] Feed '{settings.FeedId}' not found."
                : "[red]Error:[/] No feeds available. Create a feed first.");

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
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");

            return 1;
        }

        AnsiConsole.WriteLine();

        // Delete icon
        var success = await FeedHelpers.DeleteIconAsync(httpClient, feed.Id);

        AnsiConsole.WriteLine();

        return success ? 0 : 1;
    }
}
