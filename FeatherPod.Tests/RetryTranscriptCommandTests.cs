using System.Net;
using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

/// <summary>
/// Tests for the CLI's transcript retry (EpisodeHelpers.RetryTranscriptAsync, the failed-transcript selection filter) and the job id
/// it prints under a server-side normalization failure.
/// </summary>
[Collection("Sequential")]
public class RetryTranscriptCommandTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? LastMethod { get; private set; }

        public string? LastPath { get; private set; }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Accepted;

        public string ResponseBody { get; set; } = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;

            return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = new StringContent(ResponseBody) });
        }
    }

    [Fact]
    public async Task RetryTranscriptAsync_Accepted_PostsToEpisodeRetryAndReturnsJobId()
    {
        // Arrange
        var handler = new CapturingHandler { ResponseBody = """{"jobId":"job-42","status":"Queued"}""" };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        // Act
        var result = await EpisodeHelpers.RetryTranscriptAsync(client, "my-feed", "ep1");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("job-42", result.JobId);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/api/feeds/my-feed/episodes/ep1/transcript/retry", handler.LastPath);
    }

    [Fact]
    public async Task RetryTranscriptAsync_Refused_ReturnsServerReason()
    {
        // Arrange
        var handler = new CapturingHandler { StatusCode = HttpStatusCode.Conflict, ResponseBody = """{"error":"Nothing to retry","reason":"not-retryable"}""" };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

        // Act
        var result = await EpisodeHelpers.RetryTranscriptAsync(client, "my-feed", "ep1");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("not-retryable", result.Reason);
        Assert.Equal("Nothing to retry", result.ErrorMessage);
    }

    [Fact]
    public void WithFailedTranscript_OffersOnlyEpisodesWhoseTranscriptFailed()
    {
        // Arrange
        List<Episode> episodes = [NewEpisode("a", TranscriptStatus.Failed), NewEpisode("b", TranscriptStatus.Available), NewEpisode("c", null), NewEpisode("d", TranscriptStatus.Failed)];

        // Act
        var eligible = EpisodeHelpers.WithFailedTranscript(episodes);

        // Assert
        Assert.Equal(["a", "d"], eligible.Select(e => e.Id));
    }

    [Fact]
    public void NormalizationRetryHint_ShownOnlyForAJobThatFailed()
    {
        // Arrange: the outcomes the CLI's progress stream reports
        var failed = new NormalizationProgressRenderer.NormalizationResult(false, Error: "ffmpeg exited with code 1", NormalizationFailed: true);
        var cancelled = new NormalizationProgressRenderer.NormalizationResult(false, Error: "Job was cancelled");
        var dropped = new NormalizationProgressRenderer.NormalizationResult(false, Error: "Connection closed unexpectedly");
        var completed = new NormalizationProgressRenderer.NormalizationResult(true, EpisodeId: "ep1");

        // Act & Assert: only a failed job is one the retry endpoint accepts
        Assert.True(EpisodeHelpers.ShowsNormalizationRetryHint(failed));
        Assert.False(EpisodeHelpers.ShowsNormalizationRetryHint(cancelled));
        Assert.False(EpisodeHelpers.ShowsNormalizationRetryHint(dropped));
        Assert.False(EpisodeHelpers.ShowsNormalizationRetryHint(completed));
    }

    [Theory]
    [InlineData(nameof(JobStatus.Failed), "ffmpeg exited with code 1", true)]
    [InlineData(nameof(JobStatus.Failed), null, false)]
    [InlineData(nameof(JobStatus.Cancelled), null, false)]
    public void RetryableNormalizationFailure_OnlyAFailedJobWithANormalizationError(string status, string? normalizationError, bool expected)
    {
        // Arrange: a failed publication (EpisodeNotPublishedError) is Failed with only Error set
        var response = new JobStatusResponse { JobId = "j", Status = status, Error = normalizationError ?? "The episode could not be published", NormalizationError = normalizationError };

        // Act
        var retryable = EpisodeHelpers.IsRetryableNormalizationFailure(response);

        // Assert
        Assert.Equal(expected, retryable);
    }

    [Fact]
    public void NormalizationFailureJobHint_IncludesJobId()
    {
        // Arrange
        const string jobId = "0123abcd";

        // Act
        var hint = EpisodeHelpers.NormalizationFailureJobHint(jobId);

        // Assert
        Assert.Contains($"Job ID: [grey]{jobId}[/]", hint);
        Assert.Contains($"/api/jobs/{jobId}/retry", hint);
        Assert.DoesNotContain("push page", hint);
    }

    private static Episode NewEpisode(string id, TranscriptStatus? status) => new()
    {
        Id = id,
        FeedId = "my-feed",
        Title = "Episode " + id,
        FileName = id + ".mp3",
        FileSize = 1,
        Duration = TimeSpan.FromMinutes(1),
        PublishedDate = DateTime.UtcNow,
        UploadedAt = DateTime.UtcNow,
        Source = UploadSource.Browser,
        TranscriptStatus = status,
    };
}
