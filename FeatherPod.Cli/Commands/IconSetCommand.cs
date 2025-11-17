using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands;

internal sealed class IconSetCommand : AsyncCommand<IconSetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, IconSetSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Icon Upload[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

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

        // Upload icon
        var success = await CliHelpers.UploadIconAsync(httpClient, feed.Id, iconPath);

        AnsiConsole.WriteLine();
        return success ? 0 : 1;
    }
}
