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

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{feedId}/episodes?normalize=true");
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

    [GeneratedRegex(@"""jobId"":""([^""]+)""")]
    private static partial Regex JobIdRegex();
}
