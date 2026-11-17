using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Reads the transcript a job's completed transcription produced, for the joins and for a normalization retry that keeps it.
/// </summary>
internal static class StagedTranscripts
{
    /// <summary>
    /// The transcript staged for the job's current attempt. A job written before attempts existed (no <see cref="JobStatusEntity.Attempt"/>)
    /// was transcribed by a run that wrote the episode's transcript location directly, so for such a job a missing staged transcript
    /// falls back to that location: the old writer put this job's own result there, and any other legacy writer for the same episode id
    /// transcribed the same file. Returns null when neither exists.
    /// </summary>
    public static async Task<string?> ReadCompletedAsync(IBlobStorageService blobService, JobStatusEntity job)
    {
        var staged = await blobService.DownloadPendingTranscriptAsync(job.FeedId!, job.RowKey, job.GetAttempt());
        if (staged != null || job.Attempt != null || job.EpisodeId == null)
        {
            return staged;
        }

        await using var legacy = await blobService.DownloadTranscriptAsync(job.FeedId!, job.EpisodeId);
        if (legacy == null)
        {
            return null;
        }

        using var reader = new StreamReader(legacy);

        return await reader.ReadToEndAsync();
    }
}
