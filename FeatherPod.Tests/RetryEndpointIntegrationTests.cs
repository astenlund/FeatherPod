using System.Net;
using System.Text.Json;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FeatherPod.Tests;

/// <summary>
/// The retry endpoints and the RSS feed over HTTP, against Azurite: a FeedOwner of another feed is refused by both retry endpoints,
/// and a published transcript retry changes the feed version, so a conditional fetch with the earlier ETag gets the new feed.
/// </summary>
[Collection("IntegrationTests")]
public sealed class RetryEndpointIntegrationTests : IDisposable
{
    private readonly UserManagementWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public RetryEndpointIntegrationTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [AzuriteFact]
    public async Task RetryEndpoints_FeedOwnerOfAnotherFeed_Forbidden()
    {
        // Arrange
        var feedId = "rt-" + Guid.NewGuid().ToString("N")[..8];
        await CreateFeedAsync(feedId);
        var otherKey = await CreateUserAsync("rto" + Guid.NewGuid().ToString("N")[..6], ["some-other-feed"]);
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        await jobs.InitializeAsync();
        var jobId = "rtj-" + Guid.NewGuid().ToString("N")[..10];
        await jobs.CreateJobStatusAsync(new CreateJobOptions { JobId = jobId, FeedId = feedId, FileName = "f.mp3", EpisodeId = "ep1" });

        // Act
        var transcript = await PostAsync($"/api/feeds/{feedId}/episodes/ep1/transcript/retry", otherKey);
        var normalization = await PostAsync($"/api/jobs/{jobId}/retry", otherKey);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, transcript.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, normalization.StatusCode);
    }

    [AzuriteFact]
    public async Task PublishedTranscriptRetry_ConditionalFeedFetchWithEarlierETag_GetsNewFeedWithTranscript()
    {
        // Arrange: an episode with a failed transcript, and the feed's ETag before the retry publishes
        var feedId = "rt-" + Guid.NewGuid().ToString("N")[..8];
        await CreateFeedAsync(feedId);
        var episodes = _factory.Services.GetRequiredService<EpisodeService>();
        var uploadedAt = DateTime.UtcNow;
        await episodes.AddEpisodeFromEntityAsync(new Episode
        {
            Id = "ep1",
            FeedId = feedId,
            Title = "Retry Episode",
            FileName = "show.mp3",
            FileSize = 3,
            Duration = TimeSpan.FromMinutes(1),
            PublishedDate = DateTime.UtcNow,
            Source = UploadSource.Browser,
            UploadedAt = uploadedAt,
            TranscriptStatus = TranscriptStatus.Failed,
        });
        var before = await _client.GetAsync($"/{feedId}/feed.xml");
        var earlierETag = before.Headers.ETag!;
        var blob = _factory.Services.GetRequiredService<IBlobStorageService>();

        // Act
        var published = await episodes.PublishTranscriptIfAsync(feedId, "ep1", uploadedAt, () => blob.UploadTranscriptAsync(feedId, "ep1", "WEBVTT\n\nretried"));
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{feedId}/feed.xml");
        request.Headers.IfNoneMatch.Add(earlierETag);
        var after = await _client.SendAsync(request);

        // Assert
        Assert.True(published);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Contains("podcast:transcript", await after.Content.ReadAsStringAsync());
        Assert.DoesNotContain("podcast:transcript", await before.Content.ReadAsStringAsync());
    }

    [AzuriteFact]
    public async Task ConcurrentNormalizationRetries_StartExactlyOneRerun()
    {
        // Arrange: a job failed in normalization whose pending source is still there
        var feedId = "rt-" + Guid.NewGuid().ToString("N")[..8];
        await CreateFeedAsync(feedId);
        var jobs = _factory.Services.GetRequiredService<IJobService>();
        await jobs.InitializeAsync();
        var jobId = "rtj-" + Guid.NewGuid().ToString("N")[..10];
        await jobs.CreateJobStatusAsync(new CreateJobOptions { JobId = jobId, FeedId = feedId, FileName = "f.mp3", EpisodeId = "ep1" });
        await jobs.UpdateJobStatusAsync(jobId, e =>
        {
            e.Status = nameof(JobStatus.Failed);
            e.NormalizationComplete = true;
            e.NormalizationError = "ffmpeg exited with code 1";
            e.CompletedAt = DateTimeOffset.UtcNow;
        });
        var source = Path.GetTempFileName();
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await _factory.Services.GetRequiredService<IBlobStorageService>().UploadPendingAudioAsync(feedId, jobId, "f.mp3", source);
        File.Delete(source);

        // Act
        var responses = await Task.WhenAll(PostAsync($"/api/jobs/{jobId}/retry", UserManagementWebApplicationFactory.ApiKey), PostAsync($"/api/jobs/{jobId}/retry", UserManagementWebApplicationFactory.ApiKey));

        // Assert: one accepted; two resets would have left the job on attempt 3
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Equal(2, (await jobs.GetJobStatusAsync(jobId))!.Attempt);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-API-Key", apiKey);

        return await _client.SendAsync(request);
    }

    private async Task CreateFeedAsync(string feedId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds")
        {
            Content = new StringContent($$"""{"id":"{{feedId}}","title":"Retry Podcast","description":"d","author":"a","email":"e@example.com","language":"en","category":"Technology"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-API-Key", UserManagementWebApplicationFactory.ApiKey);
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private async Task<string> CreateUserAsync(string userId, string[] ownedFeeds)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users")
        {
            Content = new StringContent($$"""{"id":"{{userId}}","name":"n","email":"{{userId}}@example.com","role":"FeedOwner","ownedFeeds":{{JsonSerializer.Serialize(ownedFeeds)}}}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-API-Key", UserManagementWebApplicationFactory.ApiKey);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("apiKey").GetString()!;
    }
}
