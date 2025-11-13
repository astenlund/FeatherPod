using FeatherPod.Cli.Infrastructure;
using FeatherPod.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Cli.Commands;

internal sealed class PushCommand : AsyncCommand<PushSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PushSettings settings, CancellationToken cancellationToken)
    {
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
        AnsiConsole.WriteLine();

        var successCount = 0;
        var failureCount = 0;

        foreach (var file in files)
        {
            var success = await CliHelpers.UploadEpisodeAsync(httpClient, file, settings);
            if (success)
                successCount++;
            else
                failureCount++;

            AnsiConsole.WriteLine();
        }

        // Summary
        AnsiConsole.WriteLine();
        if (successCount > 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Successfully uploaded: {successCount}");
        }

        if (failureCount > 0)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed: {failureCount}");
        }

        return failureCount == 0 ? 0 : 1;
    }
}
