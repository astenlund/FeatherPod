using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Tests;

[Collection("IntegrationTests")]
public sealed class InputValidationIntegrationTests : IDisposable
{
    private readonly InputValidationWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private const string TestFeedId = "valid-feed";

    public InputValidationIntegrationTests()
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
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    // ============================================================================
    // FEEDS CONTROLLER VALIDATION TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task GetFeed_ShouldReturn400_WhenFeedIdContainsSpace()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/invalid%20feed");
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("error", content);
    }

    [AzuriteFact]
    public async Task GetFeed_ShouldReturn400_WhenFeedIdContainsPathTraversal()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/..%2F..%2Fetc");
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task CreateFeed_ShouldReturn400_WhenFeedIdTooLong()
    {
        var longId = new string('a', 65);
        var feedJson = $$"""
        {
            "id": "{{longId}}",
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
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task CreateFeed_ShouldSucceed_WhenFeedIdContainsDots()
    {
        const string feedJson = """
                                {
                                    "id": "my.podcast.feed",
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
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [AzuriteFact]
    public async Task DeleteFeed_ShouldReturn400_WhenFeedIdInvalid()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/feeds/invalid%2Ffeed");
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ============================================================================
    // EPISODES CONTROLLER VALIDATION TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task ListEpisodes_ShouldReturn400_WhenFeedIdInvalid()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/invalid%20feed/episodes");
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task UploadEpisode_ShouldReturn400_WhenFilenameContainsPathTraversal()
    {
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("fake audio data"u8.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "../../../etc/passwd");
        content.Add(new StringContent("Test Episode"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid", responseContent, StringComparison.OrdinalIgnoreCase);
    }

    [AzuriteFact]
    public async Task UploadEpisode_ShouldReturn400_WhenFilenameContainsBackslash()
    {
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("fake audio data"u8.ToArray());
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "..\\..\\windows\\system32\\config");
        content.Add(new StringContent("Test Episode"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", InputValidationWebApplicationFactory.ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ============================================================================
    // PUBLIC ENDPOINT VALIDATION TESTS (feed.xml, icon.png, audio)
    // ============================================================================

    [AzuriteFact]
    public async Task FeedXml_ShouldReturn400_WhenFeedIdInvalid()
    {
        var response = await _client.GetAsync("/invalid%2Ffeed/feed.xml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task IconPng_ShouldReturn400_WhenFeedIdInvalid()
    {
        var response = await _client.GetAsync("/invalid%2Ffeed/icon.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task AudioFile_ShouldReturn400_WhenFeedIdInvalid()
    {
        var response = await _client.GetAsync("/invalid%2Ffeed/audio/episode.mp3");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task AudioFile_ShouldReturn400_WhenFilenameContainsPathTraversal()
    {
        await CreateTestFeedAsync();

        var response = await _client.GetAsync($"/{TestFeedId}/audio/..%2F..%2Fetc%2Fpasswd");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task AudioFile_ShouldReturn400_WhenFilenameContainsBackslash()
    {
        await CreateTestFeedAsync();

        var response = await _client.GetAsync($"/{TestFeedId}/audio/..%5C..%5Cwindows%5Csystem32");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

internal class InputValidationWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key-12345";

    private readonly string ContainerName;

    public InputValidationWebApplicationFactory()
    {
        var testId = Guid.NewGuid().ToString("N")[..12];
        ContainerName = $"featherpod-validation-test-{testId}";
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Azure:ConnectionString"] = "UseDevelopmentStorage=true",
                ["Azure:ContainerName"] = ContainerName,
                ["Podcast:BaseUrl"] = "http://localhost:5000",
                ["ApiKey"] = ApiKey
            }!);
        });

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.AddFilter("FeatherPod.Services.EpisodeService", LogLevel.Error);
            logging.SetMinimumLevel(LogLevel.Information);
        });

        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Thread.Sleep(200);
        }
        base.Dispose(disposing);
    }
}
