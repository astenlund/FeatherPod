using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace FeatherPod.Tests;

/// <summary>
/// Integration tests for job progress endpoints (polling and SSE).
/// </summary>
[Collection("IntegrationTests")]
public partial class JobProgressIntegrationTests : IDisposable
{
    private readonly FeatherPodWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private const string TestFeedId = "progress-test-feed";

    public JobProgressIntegrationTests()
    {
        _factory = new();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task CreateTestFeedAsync(string feedId = TestFeedId)
    {
        var feedJson = $$"""
        {
            "id": "{{feedId}}",
            "title": "Test Podcast",
            "description": "Test Description",
            "author": "Test Author",
            "email": "test@example.com",
            "language": "en",
            "category": "Technology"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds");
        request.Content = new StringContent(feedJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> UploadWithNormalizeAsync(string feedId = TestFeedId)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("fake audio data"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test-normalize.mp3");
        content.Add(new StringContent("Normalize Test Episode"), "title");

        var url = $"/api/feeds/{feedId}/episodes?normalize=true";

        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var responseContent = await response.Content.ReadAsStringAsync();
        var jobIdMatch = JobIdRegex().Match(responseContent);
        Assert.True(jobIdMatch.Success, "Response should contain jobId");

        return jobIdMatch.Groups[1].Value;
    }

    [AzuriteFact]
    public async Task GetJobStatus_ShouldReturnJobWithProgressFields()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        // Act
        var response = await _client.GetAsync($"/api/jobs/{jobId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();

        // Should contain progress-related fields
        Assert.Contains("stage", content);
        Assert.Contains("progressPercent", content);
    }

    [AzuriteFact]
    public async Task GetJobStatus_ForNonExistentJob_ShouldReturn404()
    {
        // Act
        var response = await _client.GetAsync("/api/jobs/nonexistent-job-id");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [AzuriteFact]
    public async Task StreamJobProgress_ShouldReturnSSEContentType()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        // Act - use HttpCompletionOption.ResponseHeadersRead to get the response before body is complete
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await _client.GetAsync(
            $"/api/jobs/{jobId}/progress",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [AzuriteFact]
    public async Task StreamJobProgress_ForNonExistentJob_ShouldSendErrorEvent()
    {
        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await _client.GetAsync(
            "/api/jobs/nonexistent-job-id/progress",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        // Assert - should get a response with SSE content type
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // Read the SSE stream
        var events = await ReadSSEEventsAsync(response, cts.Token, maxEvents: 2);

        // Should receive an error event
        Assert.Contains(events, e => e.eventType == "error");
    }

    [AzuriteFact]
    public async Task StreamJobProgress_ShouldSendProgressEvents()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await _client.GetAsync(
            $"/api/jobs/{jobId}/progress",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await ReadSSEEventsAsync(response, cts.Token, maxEvents: 3);

        // Should receive at least one progress event with valid JSON
        var progressEvent = events.FirstOrDefault(e => e.eventType == "progress");
        Assert.NotNull(progressEvent.data);
        Assert.Contains("stage", progressEvent.data);
    }

    [AzuriteFact]
    public async Task StreamJobProgress_ShouldIncludeCacheControlHeaders()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await _client.GetAsync(
            $"/api/jobs/{jobId}/progress",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Check for SSE-specific headers
        Assert.True(response.Headers.Contains("Cache-Control"));
        var cacheControl = response.Headers.GetValues("Cache-Control").FirstOrDefault();
        Assert.Contains("no-cache", cacheControl ?? "");
    }

    /// <summary>
    /// Helper to read SSE events from a streaming response.
    /// </summary>
    private static async Task<List<(string eventType, string data)>> ReadSSEEventsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        int maxEvents = 10)
    {
        var events = new List<(string eventType, string data)>();
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? currentEvent = null;

        try
        {
            while (events.Count < maxEvents && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                if (line.StartsWith("event: "))
                {
                    currentEvent = line[7..];
                }
                else if (line.StartsWith("data: ") && currentEvent != null)
                {
                    events.Add((currentEvent, line[6..]));
                    currentEvent = null;

                    // If we got a done or error event, stop reading
                    if (events.Last().eventType is "done" or "error")
                    {
                        break;
                    }
                }
                else if (line.StartsWith(":"))
                {
                    // Comment/heartbeat - ignore
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout - return what we have
        }
        catch (IOException)
        {
            // Connection closed - return what we have
        }

        return events;
    }

    // ============================================================================
    // CANCEL JOB TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task CancelJob_ForNonExistentJob_ShouldReturn404()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs/nonexistent-job-id/cancel");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [AzuriteFact]
    public async Task CancelJob_WithoutAuth_ShouldReturn401()
    {
        // Act
        var response = await _client.PostAsync("/api/jobs/some-job-id/cancel", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [AzuriteFact]
    public async Task CancelJob_ForQueuedJob_ShouldReturn200WithCancelledStatus()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/cancel");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Cancelled\"", content);
        Assert.Contains("\"stage\":\"Cancelled\"", content);
    }

    [AzuriteFact]
    public async Task CancelJob_AlreadyCancelled_ShouldReturn409()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        // Cancel once
        var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/cancel");
        cancelRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var firstResponse = await _client.SendAsync(cancelRequest);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // Act - cancel again
        var secondRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/cancel");
        secondRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var response = await _client.SendAsync(secondRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [AzuriteFact]
    public async Task CancelJob_ShouldCleanUpPendingBlobs()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/cancel");
        cancelRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(cancelRequest);

        // Assert - job is cancelled
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify job status shows Cancelled with CompletedAt
        var statusResponse = await _client.GetAsync($"/api/jobs/{jobId}");
        var statusContent = await statusResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Cancelled\"", statusContent);
        Assert.Contains("\"completedAt\"", statusContent);
    }

    [AzuriteFact]
    public async Task CancelJob_FeedOwnerOfDifferentFeed_ShouldReturn403()
    {
        // Arrange - create a feed and upload a job as admin
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        // Create a FeedOwner who owns a different feed
        var feedOwnerKey = await CreateTestUserAsync("other-owner", "FeedOwner", ["some-other-feed"]);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/cancel");
        request.Headers.Add("X-API-Key", feedOwnerKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [AzuriteFact]
    public async Task CancelJob_FeedOwnerOfSameFeed_ShouldReturn200()
    {
        // Arrange - create a feed owner who owns the test feed
        var feedOwnerKey = await CreateTestUserAsync("feed-owner", "FeedOwner", [TestFeedId]);

        // Create feed and upload as admin (feed owner can't create feeds)
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/cancel");
        request.Headers.Add("X-API-Key", feedOwnerKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Cancelled\"", content);
    }

    [AzuriteFact]
    public async Task CancelJob_ActiveJobsEndpoint_ShouldExcludeCancelledJob()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId1 = await UploadWithNormalizeAsync();
        var jobId2 = await UploadWithNormalizeAsync();

        // Cancel one of the two jobs
        var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId1}/cancel");
        cancelRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var cancelResponse = await _client.SendAsync(cancelRequest);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        // Act — fetch active jobs (the endpoint the push page calls on refresh)
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/feeds/{TestFeedId}/jobs");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(jobId1, content);
        Assert.Contains(jobId2, content);
    }

    [AzuriteFact]
    public async Task CancelJob_DuringSSEStream_ShouldDeliverCancelledEvent()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        // Start SSE stream (push mode subscribes to IJobProgressChannel)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sseResponse = await _client.GetAsync(
            $"/api/jobs/{jobId}/progress",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResponse.StatusCode);

        // Read initial progress event
        var stream = await sseResponse.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var initialEvents = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null || line == string.Empty) break;
            initialEvents.Add(line);
        }

        // Act — cancel the job while SSE is streaming
        var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/cancel");
        cancelRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var cancelResponse = await _client.SendAsync(cancelRequest);
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        // Assert — SSE should deliver a progress event with Cancelled status, then done
        var events = new List<(string eventType, string data)>();
        string? currentEvent = null;
        try
        {
            while (events.Count < 5 && !cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line == null) break;
                if (line.StartsWith("event: ")) currentEvent = line[7..];
                else if (line.StartsWith("data: ") && currentEvent != null)
                {
                    events.Add((currentEvent, line[6..]));
                    currentEvent = null;
                    if (events.Last().eventType is "done" or "error") break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }

        var cancelEvent = events.FirstOrDefault(e => e.eventType == "progress" && e.data.Contains("Cancelled"));
        Assert.NotNull(cancelEvent.data);
        Assert.Contains(events, e => e.eventType == "done");
    }

    [AzuriteFact]
    public async Task StreamJobProgress_ForCancelledJob_ShouldSendDoneEvent()
    {
        // Arrange
        await CreateTestFeedAsync();
        var jobId = await UploadWithNormalizeAsync();

        // Cancel the job
        var cancelRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/cancel");
        cancelRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(cancelRequest);

        // Act - stream progress for the cancelled job
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await _client.GetAsync(
            $"/api/jobs/{jobId}/progress",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadSSEEventsAsync(response, cts.Token, maxEvents: 5);

        // Should get a progress event with Cancelled status, followed by done
        var progressEvent = events.FirstOrDefault(e => e.eventType == "progress");
        Assert.NotNull(progressEvent.data);
        Assert.Contains("Cancelled", progressEvent.data);

        Assert.Contains(events, e => e.eventType == "done");
    }

    // ============================================================================
    // HELPERS
    // ============================================================================

    private async Task<string> CreateTestUserAsync(string userId, string role = "FeedOwner", string[]? ownedFeeds = null)
    {
        var userJson = $$"""
        {
            "id": "{{userId}}",
            "name": "{{userId}} Name",
            "email": "{{userId}}@example.com",
            "role": "{{role}}",
            "ownedFeeds": {{System.Text.Json.JsonSerializer.Serialize(ownedFeeds ?? [])}}
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users");
        request.Content = new StringContent(userJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(responseJson);

        return doc.GetProperty("apiKey").GetString()!;
    }

    [GeneratedRegex(@"""jobId"":""([^""]+)""")]
    private static partial Regex JobIdRegex();
}
