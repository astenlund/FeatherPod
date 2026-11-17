namespace FeatherPod.Tests.Helpers;

/// <summary>
/// <see cref="StubBlobService"/> that records transcript uploads and the pending-blob traffic of a transcription
/// (downloads, converted-audio uploads and SAS URL requests) so tests can assert on what got read and written.
/// </summary>
public sealed class RecordingBlobService : StubBlobService
{
    public List<(string FeedId, string EpisodeId, string Vtt)> UploadedTranscripts { get; } = [];

    /// <summary>
    /// Transcripts staged by transcription runs (they publish to the episode's location only through their job's join).
    /// </summary>
    public List<(string FeedId, string JobId, int Attempt, string Vtt)> StagedTranscripts { get; } = [];

    public override Task UploadPendingTranscriptAsync(string feedId, string jobId, int attempt, string vttContent)
    {
        StagedTranscripts.Add((feedId, jobId, attempt, vttContent));

        return Task.CompletedTask;
    }

    public List<string> AudioDownloads { get; } = [];

    public List<string> AudioSasRequests { get; } = [];

    public override Task<Stream> DownloadAudioAsync(string feedId, string fileName)
    {
        AudioDownloads.Add(fileName);

        return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
    }

    public override Task<string> GenerateAudioSasUrlAsync(string feedId, string fileName)
    {
        AudioSasRequests.Add(fileName);

        return Task.FromResult($"https://blob.example/audio/{fileName}");
    }

    public List<string> PendingDownloads { get; } = [];

    public List<(string FileName, bool SourceExisted)> PendingAudioUploads { get; } = [];

    public List<string> SasRequests { get; } = [];

    public override Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent)
    {
        UploadedTranscripts.Add((feedId, episodeId, vttContent));

        return Task.CompletedTask;
    }

    public override Task<Stream> DownloadPendingBlobAsync(string feedId, string jobId, string fileName)
    {
        PendingDownloads.Add(fileName);

        return base.DownloadPendingBlobAsync(feedId, jobId, fileName);
    }

    public override Task UploadPendingAudioAsync(string feedId, string jobId, string fileName, string filePath, CancellationToken cancellationToken = default)
    {
        PendingAudioUploads.Add((fileName, File.Exists(filePath)));

        return Task.CompletedTask;
    }

    public override Task<string> GeneratePendingBlobSasUrlAsync(string feedId, string jobId, string fileName)
    {
        SasRequests.Add(fileName);

        return Task.FromResult(SasUrlFor(fileName));
    }

    /// <summary>
    /// The SAS URL this stub hands out for <paramref name="fileName"/>.
    /// </summary>
    public static string SasUrlFor(string fileName) => $"https://blob.example/pending/{fileName}";
}
