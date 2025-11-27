using System.Net;
using System.Text.Json;

namespace FeatherPod.Tests;

[Collection("IntegrationTests")]
public class HealthCheckIntegrationTests : IDisposable
{
    private readonly FeatherPodWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public HealthCheckIntegrationTests()
    {
        _factory = new FeatherPodWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [AzuriteFact]
    public async Task Health_ShouldReturnOk_WithHealthyStatus()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.Equal("Healthy", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("feedCount", out var feedCount));
        Assert.True(feedCount.GetInt32() >= 0);
        Assert.True(root.TryGetProperty("timestamp", out _));
    }

    [AzuriteFact]
    public async Task Health_ShouldNotRequireAuthentication()
    {
        // Act - No API key header
        var response = await _client.GetAsync("/health");

        // Assert - Should still succeed (public endpoint)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [AzuriteFact]
    public async Task Health_ShouldReflectFeedCount_AfterCreatingFeed()
    {
        // Arrange - Create a feed
        var feedJson = """
        {
            "id": "health-test-feed",
            "title": "Health Test Podcast",
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
        await _client.SendAsync(request);

        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var feedCount = doc.RootElement.GetProperty("feedCount").GetInt32();

        Assert.True(feedCount >= 1);
    }
}
