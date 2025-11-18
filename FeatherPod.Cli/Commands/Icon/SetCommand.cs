using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings.Icon;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands.Icon;

internal sealed class SetCommand : AsyncCommand<SetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SetSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Icon Upload[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null)
        {
            return 1;
        }

        // Validate icon file exists
        var iconPath = settings.IconPath.Trim().Trim('"', '\'');
        if (!File.Exists(iconPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Icon file not found: {Markup.Escape(iconPath)}");

            return 1;
        }

        // Validate file extension
        var extension = Path.GetExtension(iconPath).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Icon must be a PNG or JPEG file");

            return 1;
        }

        // Select feed (use -f flag if provided, otherwise prompt user to select)
        var feed = !string.IsNullOrEmpty(settings.FeedId)
            ? await CliHelpers.GetFeedByIdAsync(httpClient, settings.FeedId)
            : await CliHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);

        if (feed == null)
        {
            AnsiConsole.MarkupLine(!string.IsNullOrEmpty(settings.FeedId)
                ? $"[red]Error:[/] Feed '{settings.FeedId}' not found."
                : "[red]Error:[/] No feeds available. Create a feed first.");

            return 1;
        }

        // Upload icon
        var success = await CliHelpers.UploadIconAsync(httpClient, feed.Id, iconPath);

        AnsiConsole.WriteLine();

        return success ? 0 : 1;
    }
}
