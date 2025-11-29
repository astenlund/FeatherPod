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

                void UpdateProgress(ProgressUpdate update)
                {
                    lastUpdate = update;
                    task.Value = update.ProgressPercent;

                    var stageDesc = NormalizationProgressHelper.GetStageDescription(update.Stage.ToString());
                    var position = NormalizationProgressHelper.FormatPosition(update.CurrentPosition, update.TotalDuration);

                    task.Description = string.IsNullOrEmpty(position)
                        ? stageDesc
                        : $"{stageDesc} [grey]{position}[/]";
                }

                result = await operation(UpdateProgress, cancellationToken);

                // Update final state with position
                task.Value = 100;
                if (lastUpdate != null)
                {
                    var stageDesc = NormalizationProgressHelper.GetStageDescription(lastUpdate.Stage.ToString());
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
