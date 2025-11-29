using FeatherPod.Shared.Models;
using Spectre.Console;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Shared progress bar renderer for audio normalization.
/// Used by both local (FFmpeg) and server-side (SSE) normalization paths.
/// </summary>
public static class NormalizationProgressRenderer
{
    /// <summary>
    /// Result of a normalization operation with optional metadata.
    /// </summary>
    public record NormalizationResult(bool Success, string? Error = null, string? EpisodeId = null);

    /// <summary>
    /// Run an async operation with progress bar display.
    /// </summary>
    /// <param name="fileName">File name to display in progress bar</param>
    /// <param name="operation">Async operation that calls the progress callback</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the normalization operation</returns>
    public static async Task<NormalizationResult> RunWithProgressAsync(
        string fileName,
        Func<Action<ProgressUpdate>, CancellationToken, Task<NormalizationResult>> operation,
        CancellationToken cancellationToken = default)
    {
        NormalizationResult result = new(false);
        ProgressUpdate? lastUpdate = null;
        var passedQueued = false;
        Action<ProgressUpdate>? progressHandler = null;

        // Start operation - updates come via callback
        var operationTask = operation(update =>
        {
            lastUpdate = update;
            if (update.Stage != NormalizationStage.Queued)
            {
                passedQueued = true;
            }
            progressHandler?.Invoke(update);
        }, cancellationToken);

        // Show "Queued" spinner while waiting
        if (!passedQueued && !operationTask.IsCompleted)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Queued", async _ =>
                {
                    while (!passedQueued && !operationTask.IsCompleted)
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                });
        }

        if (operationTask.IsCompleted)
        {
            return await operationTask;
        }

        // Show progress bar for actual work
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new SpinnerColumn(),
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn { CompletedStyle = Style.Plain })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(Markup.Escape(fileName), maxValue: 100);

                progressHandler = update =>
                {
                    if (update.Stage == NormalizationStage.Queued) return;

                    task.Value = update.ProgressPercent;
                    var stageDesc = NormalizationProgressHelper.GetStageDescription(update);
                    var position = NormalizationProgressHelper.FormatPosition(update.CurrentPosition, update.TotalDuration);

                    task.Description = string.IsNullOrEmpty(position)
                        ? stageDesc
                        : $"{stageDesc} [grey]{position}[/]";
                };

                // Apply last update if we missed it
                if (lastUpdate != null && lastUpdate.Stage != NormalizationStage.Queued)
                {
                    progressHandler(lastUpdate);
                }

                result = await operationTask;

                // Final state
                task.Value = 100;
                if (lastUpdate != null)
                {
                    var stageDesc = NormalizationProgressHelper.GetStageDescription(lastUpdate);
                    var position = NormalizationProgressHelper.FormatPosition(lastUpdate.TotalDuration, lastUpdate.TotalDuration);
                    task.Description = string.IsNullOrEmpty(position)
                        ? stageDesc
                        : $"{stageDesc} [grey]{position}[/]";
                }
                task.StopTask();
            });

        return result;
    }

    /// <summary>
    /// Display the result of a normalization operation.
    /// </summary>
    public static void DisplayResult(NormalizationResult result)
    {
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Normalization complete");

            if (!string.IsNullOrEmpty(result.EpisodeId))
            {
                AnsiConsole.MarkupLine($"  Episode ID: [grey]{result.EpisodeId}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Normalization failed: {Markup.Escape(result.Error ?? "Unknown error")}");
        }
    }
}
