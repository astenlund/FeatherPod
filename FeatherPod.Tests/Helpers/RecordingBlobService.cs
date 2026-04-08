namespace FeatherPod.Tests.Helpers;

/// <summary>
/// <see cref="StubBlobService"/> that records every transcript upload so tests can assert
/// on what got written.
/// </summary>
public sealed class RecordingBlobService : StubBlobService
{
    public List<(string FeedId, string EpisodeId, string Vtt)> UploadedTranscripts { get; } = [];

    public override Task UploadTranscriptAsync(string feedId, string episodeId, string vttContent)
    {
        UploadedTranscripts.Add((feedId, episodeId, vttContent));

        return Task.CompletedTask;
    }
}
