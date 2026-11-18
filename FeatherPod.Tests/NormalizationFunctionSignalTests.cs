using Azure;
using FeatherPod.Functions;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

/// <summary>
/// The normalization-complete signal a successful Function run sends.
/// </summary>
public sealed class NormalizationFunctionSignalTests
{
    [Theory]
    [InlineData("\"0x8DC0FFEE\"")]
    [InlineData("0x8DC0FFEE")]
    public void SuccessSignal_CarriesTheUploadedBlobsETagInHeaderForm(string reportedETag)
    {
        // Arrange: the SDK reports an upload's ETag quoted, a download's unquoted; the signal must carry the one recorded form
        var job = new NormalizationJob
        {
            JobId = "job-signal",
            FeedId = "feed",
            FileName = "show.mp3",
            OriginalFileSize = 3,
            EpisodeId = "ep",
            Title = "t",
            PublishedDate = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
            Attempt = 2,
        };

        // Act
        var signal = NormalizationFunction.SuccessSignal(job, 1234, TimeSpan.FromSeconds(61), new ETag(reportedETag));

        // Assert
        Assert.True(signal.Success);
        Assert.Equal("\"0x8DC0FFEE\"", signal.AudioETag);
        Assert.Equal(1234, signal.NormalizedFileSize);
        Assert.Equal(61000, signal.AudioDurationMs);
        Assert.Equal(2, signal.Attempt);
    }
}
