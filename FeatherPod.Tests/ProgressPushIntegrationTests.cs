using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace FeatherPod.Tests;

/// <summary>
/// Integration tests for the progress push feature:
/// internal push endpoint, mode/interval passthrough, and CreateQueued factory.
/// </summary>
[Collection("IntegrationTests")]
public partial class ProgressPushIntegrationTests : IDisposable
{
    private readonly FeatherPodWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private const string TestFeedId = "push-test-feed";

    public ProgressPushIntegrationTests()
    {
        _factory = new();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // ============================================================================
    // INTERNAL PUSH ENDPOINT
    // ============================================================================

    [AzuriteFact]
    public async Task PushJobProgress_WithValidKey_ShouldReturn200()
    {
        // Arrange
        var progress = new JobStatusResponse
        {
            JobId = "test-job-123",
            Status = "Processing",
            Stage = "Normalizing",
            ProgressPercent = 50,
            ProgressMessage = "Normalizing audio"
        };
        var json = JsonSerializer.Serialize(progress, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/internal/jobs/test-job-123/progress");
        request.Content = content;
        request.Headers.Add("X-Internal-Key", FeatherPodWebApplicationFactory.InternalKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [AzuriteFact]
    public async Task PushJobProgress_WithInvalidKey_ShouldReturn401()
    {
        // Arrange
        var progress = new JobStatusResponse
        {
            JobId = "test-job-456",
            Status = "Processing",
            Stage = "Analyzing",
            ProgressPercent = 25
        };
        var json = JsonSerializer.Serialize(progress, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/internal/jobs/test-job-456/progress");
        request.Content = content;
        request.Headers.Add("X-Internal-Key", "wrong-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [AzuriteFact]
    public async Task PushJobProgress_WithMissingKey_ShouldReturn401()
    {
        // Arrange
        var progress = new JobStatusResponse
        {
            JobId = "test-job-789",
            Status = "Processing",
            Stage = "Analyzing",
            ProgressPercent = 10
        };
        var json = JsonSerializer.Serialize(progress, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act — no X-Internal-Key header
        var response = await _client.PostAsync("/api/internal/jobs/test-job-789/progress", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ============================================================================
    // SIGNALR HUB
    // ============================================================================

    [AzuriteFact]
    public async Task SignalRHub_SendProgress_ShouldPublishToChannel()
    {
        // Arrange
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(
                _client.BaseAddress + "api/internal/signalrhub?key=" + FeatherPodWebApplicationFactory.InternalKey,
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                })
            .Build();

        await hubConnection.StartAsync();

        var progressChannel = _factory.Services.GetRequiredService<IJobProgressChannel>();
        var reader = progressChannel.Subscribe("signalr-test-job");

        var progress = new JobStatusResponse
        {
            JobId = "signalr-test-job",
            Status = "Processing",
            Stage = "Normalizing",
            ProgressPercent = 42,
            ProgressMessage = "SignalR test"
        };

        // Act
        await hubConnection.SendAsync("SendProgress", "signalr-test-job", progress);

        // Assert — verify the progress was published to the channel
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await reader.ReadAsync(cts.Token);
        Assert.Equal("signalr-test-job", received.JobId);
        Assert.Equal(42, received.ProgressPercent);
        Assert.Equal("SignalR test", received.ProgressMessage);

        progressChannel.Unsubscribe("signalr-test-job", reader);
        await hubConnection.DisposeAsync();
    }

    [AzuriteFact]
    public async Task SignalRHub_WithInvalidKey_ShouldNotDeliverMessages()
    {
        // Arrange
        var progressChannel = _factory.Services.GetRequiredService<IJobProgressChannel>();
        var reader = progressChannel.Subscribe("rejected-job");

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(
                _client.BaseAddress + "api/internal/signalrhub?key=wrong-key",
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                })
            .Build();

        // Act — attempt connection and message send with invalid key
        try
        {
            await hubConnection.StartAsync();
            await hubConnection.SendAsync("SendProgress", "rejected-job", new JobStatusResponse
            {
                JobId = "rejected-job",
                Status = "Processing",
                Stage = "Analyzing",
                ProgressPercent = 99
            });
            await Task.Delay(500);
        }
        catch
        {
            // Connection rejected — expected
        }

        // Assert — no message should have been delivered to the channel
        Assert.False(reader.TryRead(out _), "No message should be delivered with invalid key");

        progressChannel.Unsubscribe("rejected-job", reader);
        await hubConnection.DisposeAsync();
    }

    // ============================================================================
    // CONFIG-DRIVEN PROGRESS MODE
    // ============================================================================

    [AzuriteFact]
    public async Task UploadWithNormalize_ShouldUseConfigProgressMode()
    {
        // Arrange
        await CreateTestFeedAsync();

        // Act — mode comes from PushPage:ProgressMode config (set to "push" in test factory)
        var jobId = await UploadWithNormalizeAsync();

        // Assert — job should be created with Queued status
        var statusResponse = await _client.GetAsync($"/api/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var statusContent = await statusResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"Queued\"", statusContent);
    }

    // ============================================================================
    // JOBSTATUSENTITY.CREATEQUEUED FACTORY
    // ============================================================================

    [Fact]
    public void CreateQueued_WithModeAndInterval_ShouldStoreValues()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued("job-123", "feed-1", "test.mp3", "push", 250);

        // Assert
        Assert.Equal("push", entity.ProgressMode);
        Assert.Equal(250, entity.ProgressIntervalMs);
        Assert.Equal("job-123", entity.RowKey);
        Assert.Equal("feed-1", entity.FeedId);
        Assert.Equal("test.mp3", entity.FileName);
    }

    [Fact]
    public void CreateQueued_WithoutModeAndInterval_ShouldDefaultToNull()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued("job-456", "feed-2", "audio.mp3");

        // Assert
        Assert.Null(entity.ProgressMode);
        Assert.Null(entity.ProgressIntervalMs);
    }

    [Fact]
    public void CreateQueued_WithSignalrMode_ShouldStoreSignalr()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued("job-789", "feed-3", progressMode: "signalr", progressIntervalMs: 100);

        // Assert
        Assert.Equal("signalr", entity.ProgressMode);
        Assert.Equal(100, entity.ProgressIntervalMs);
    }

    // ============================================================================
    // HELPERS
    // ============================================================================

    private async Task CreateTestFeedAsync(string feedId = TestFeedId)
    {
        var feedJson = $$"""
        {
            "id": "{{feedId}}",
            "title": "Push Test Podcast",
            "description": "Test Description",
            "author": "Test Author",
            "email": "test@example.com",
            "language": "en",
            "category": "Technology"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds");
        request.Content = new StringContent(feedJson, Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> UploadWithNormalizeAsync(string feedId = TestFeedId)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("fake audio data for push test"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "push-test.mp3");
        content.Add(new StringContent("Push Test Episode"), "title");

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

    [GeneratedRegex(@"""jobId"":""([^""]+)""")]
    private static partial Regex JobIdRegex();
}
