using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed;

internal sealed class SetIconCommand : AsyncCommand<SetIconSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SetIconSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Icon Upload[/]");
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

        // Validate icon file exists
        var iconPath = settings.IconPath.Trim().Trim('"', '\'');
        if (!File.Exists(iconPath))
        {
            Out.Error($"Icon file not found: {Markup.Escape(iconPath)}");

            return 1;
        }

        // Validate file extension
        var extension = Path.GetExtension(iconPath).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
        {
            Out.Error("Icon must be a PNG or JPEG file");

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

        // Upload icon
        var success = await FeedHelpers.UploadIconAsync(httpClient, feed.Id, iconPath);

        Out.BlankLine().Flush();

        return success ? 0 : 1;
    }
}
