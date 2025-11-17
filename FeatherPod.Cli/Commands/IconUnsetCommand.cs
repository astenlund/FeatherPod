using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands;

internal sealed class IconUnsetCommand : AsyncCommand<IconUnsetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, IconUnsetSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Icon Removal[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Select feed (use -f flag if provided, otherwise prompt user to select)
        var feed = !string.IsNullOrEmpty(settings.FeedId)
            ? await CliHelpers.GetFeedByIdAsync(httpClient, settings.FeedId)
            : await CliHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);

        if (feed == null)
        {
            if (!string.IsNullOrEmpty(settings.FeedId))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Feed '{settings.FeedId}' not found.");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No feeds available. Create a feed first.");
            }
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
        var success = await CliHelpers.DeleteIconAsync(httpClient, feed.Id);

        AnsiConsole.WriteLine();
        return success ? 0 : 1;
    }
}
