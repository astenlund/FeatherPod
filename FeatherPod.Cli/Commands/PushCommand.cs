using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings;
using FeatherPod.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands;

internal sealed class PushCommand : AsyncCommand<PushSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PushSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Episode Upload[/]");
        AnsiConsole.WriteLine();

        var env = CliHelpers.GetEnvironment(settings.Environment);
        if (env == null) return 1;

        var (httpClient, _) = await CliHelpers.SetupHttpClientAsync(env);
        if (httpClient == null) return 1;

        // Select feed (use -f flag if provided, otherwise prompt user to select)
        FeedConfig? feed;
        if (!string.IsNullOrEmpty(settings.FeedId))
        {
            feed = await CliHelpers.GetFeedByIdAsync(httpClient, settings.FeedId);
            if (feed == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Feed '{settings.FeedId}' not found.");
                return 1;
            }
        }
        else
        {
            feed = await CliHelpers.SelectFeedAsync(httpClient, env, forcePrompt: true);
            if (feed == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No feeds available. Create a feed first.");
                return 1;
            }
        }

        // Expand file patterns (wildcards and comma-separated lists)
        var files = CliHelpers.ExpandFilePatterns(settings.Files);

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No files found matching pattern: {settings.Files}");
            return 1;
        }

        // Validate that title/description aren't used with multiple files
        if (files.Count > 1)
        {
            if (!string.IsNullOrEmpty(settings.Title))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Cannot use -t/--title with multiple files (all episodes would get the same title)");
                return 1;
            }

            if (!string.IsNullOrEmpty(settings.Description))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Cannot use -d/--description with multiple files (all episodes would get the same description)");
                return 1;
            }

            if (!string.IsNullOrEmpty(settings.PublishedDate))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Warning:[/] Using -p/--published-date with multiple files will set the same date for all episodes.");
                var continueAnyway = new MenuBuilder<bool?>()
                    .WithTitle("Continue anyway?")
                    .WithHint("(arrow keys or Y/N)")
                    .AddOption("Y", "Yes", true)
                    .AddOption("N", "No", false)
                    .AllowCancel(true, false)
                    .Show();

                if (continueAnyway != true)
                {
                    AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                    return 1;
                }
                AnsiConsole.WriteLine();
            }
        }

        AnsiConsole.MarkupLine($"Found [cyan]{files.Count}[/] file(s) to upload");
        AnsiConsole.WriteLine();

        // Confirm upload
        var fileList = files.Count <= 5
            ? string.Join(", ", files.Select(f => $"[cyan]{Markup.Escape(Path.GetFileName(f))}[/]"))
            : $"[cyan]{files.Count}[/] files";

        var confirmed = new MenuBuilder<bool?>()
            .WithTitle($"Upload {fileList} to feed [cyan]{Markup.Escape(feed.Title)}[/]?")
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

        // Prompt for date source if neither -p nor -x was provided
        var effectiveSettings = settings;
        if (string.IsNullOrEmpty(settings.PublishedDate) && settings.ExtractDateFromFile == null)
        {
            var dateSource = new MenuBuilder<bool?>()
                .WithTitle("Published date source:")
                .WithHint("(arrow keys or highlighted letter, Esc to cancel)")
                .AddOption("C", "Current date/time", false)
                .AddOption("F", "Extract from file metadata", true)
                .AllowCancel()
                .Show();

            if (dateSource == null)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 1;
            }

            effectiveSettings = new()
            {
                Files = settings.Files,
                Environment = settings.Environment,
                Title = settings.Title,
                Description = settings.Description,
                PublishedDate = settings.PublishedDate,
                ExtractDateFromFile = dateSource
            };
        }

        var successCount = 0;
        var failureCount = 0;

        foreach (var file in files)
        {
            var success = await CliHelpers.UploadEpisodeAsync(httpClient, feed, file, effectiveSettings);
            if (success)
                successCount++;
            else
                failureCount++;

            AnsiConsole.WriteLine();
        }

        // Summary
        if (successCount > 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Successfully uploaded: {successCount}");
        }

        if (failureCount > 0)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed: {failureCount}");
        }

        AnsiConsole.WriteLine();

        return failureCount == 0 ? 0 : 1;
    }
}
