using FeatherPod.Infrastructure;
using FeatherPod.Settings.Feed;
using Spectre.Console;
using Spectre.Console.Cli;
using TextCopy;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Feed;

internal sealed class PushUrlCommand : AsyncCommand<PushUrlSettings>
{
    /// <summary>
    /// Core push-url operation - can be called from CLI or InteractiveCommand.
    /// Constructs the browser push page URL with API key embedded as fragment.
    /// </summary>
    public static async Task<FeedOperationResult> GetPushUrlAsync(
        string environment,
        string feedId,
        bool copyToClipboard = false)
    {
        // Get base URL from config
        var configuration = EnvironmentHelpers.BuildConfiguration(environment);
        var baseUrl = configuration["Api:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            Out.Error("Base URL not configured for this environment.");
            return new() { Success = false, ErrorMessage = "Base URL not configured" };
        }

        // Get API key from preferences
        var apiKey = PreferencesHelpers.GetApiKey(environment);
        if (string.IsNullOrEmpty(apiKey))
        {
            Out.Error($"No API key configured for {Markup.Escape(environment)} environment.");
            Out.MarkupLine("[grey]Run any command to set up your API key first.[/]");
            return new() { Success = false, ErrorMessage = "No API key configured" };
        }

        // Construct URL: {baseUrl}/{feedId}/push#{apiKey}
        var url = $"{baseUrl}/{feedId}/push#{apiKey}";

        Out.MarkupLine($"[cyan]{Markup.Escape(url)}[/]");
        Out.BlankLine();

        if (copyToClipboard)
        {
            try
            {
                await ClipboardService.SetTextAsync(url);
                Out.Success("Copied to clipboard");
            }
            catch (Exception)
            {
                Out.Warning("Could not copy to clipboard (clipboard may not be available)");
            }
        }

        return new() { Success = true, FeedId = feedId };
    }

    public override async Task<int> ExecuteAsync(CommandContext context, PushUrlSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Push URL[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        // Get feed ID - either from option or prompt
        var feedId = settings.FeedId?.Trim();
        if (string.IsNullOrWhiteSpace(feedId))
        {
            // Need to connect to list feeds for selection
            var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
            if (httpClient == null) return 1;

            var feed = await FeedHelpers.SelectFeedAsync(httpClient);
            if (feed == null)
            {
                Out.Error("No feeds available.");
                return 1;
            }
            feedId = feed.Id;
        }

        var result = await GetPushUrlAsync(env, feedId, settings.Copy);

        if (result.Success)
        {
            Out.WriteLineRaw();
        }

        return result.Success ? 0 : 1;
    }
}
