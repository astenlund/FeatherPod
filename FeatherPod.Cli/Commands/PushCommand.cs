using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings;
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

        // Expand file patterns (wildcards and comma-separated lists)
        var files = CliHelpers.ExpandFilePatterns(settings.Files);

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] No files found matching pattern: {settings.Files}");
            return 1;
        }

        AnsiConsole.MarkupLine($"Found [cyan]{files.Count}[/] file(s) to upload");

        // Prompt for date source if neither -p nor -x was provided
        var effectiveSettings = settings;
        if (string.IsNullOrEmpty(settings.PublishedDate) && settings.ExtractDateFromFile == null)
        {
            var dateSource = new MenuBuilder<bool?>()
                .WithTitle("Published date source:")
                .WithHint("(arrow keys or highlighted letter, Esc to cancel)")
                .AddOption("C", "Current date/time", false)
                .AddOption("F", "Extract from file metadata", true)
                .AllowCancel(true, null)
                .Show();

            if (dateSource == null)
            {
                AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
                return 1;
            }

            effectiveSettings = new PushSettings
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
            var success = await CliHelpers.UploadEpisodeAsync(httpClient, file, effectiveSettings);
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
