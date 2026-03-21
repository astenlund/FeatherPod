using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task CreateFeed_ShouldReturn400_WhenFeedIdContainsDots()
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

internal class InputValidationWebApplicationFactory : WebApplicationFactory<FeatherPod.Server.ServerAssemblyMarker>
{
    // API key format: fp_{userId}_{secret} where secret is 22 chars base64url
    public const string ApiKey = "fp_test-admin_AAAAAAAAAAAAAAAAAAAAAA";
    private const string TestAdminUserId = "test-admin";

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
                ["AzureOpenAI:Endpoint"] = "",
                ["AzureOpenAI:Deployment"] = ""
            }!);
        });

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.AddFilter("FeatherPod.Services.EpisodeService", LogLevel.Error);
            logging.SetMinimumLevel(LogLevel.Information);
        });

        var host = base.CreateHost(builder);

        // Seed the test admin user
        SeedTestUserAsync(host).GetAwaiter().GetResult();

        return host;
    }

    private static async Task SeedTestUserAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<Server.Services.IUserService>();

        // Load existing users first
        await userService.LoadUsersAsync();

        // Check if test user already exists
        var existingUser = await userService.GetUserByIdAsync(TestAdminUserId);
        if (existingUser != null)
        {
            return;
        }

        // Compute the hash for our known test API key
        // Key format: fp_{userId}_{secret}, secret is "AAAAAAAAAAAAAAAAAAAAAA"
        // Salt: use a fixed salt for testing (16 bytes of zeros -> base64)
        var salt = Convert.ToBase64String(new byte[16]);
        var secret = "AAAAAAAAAAAAAAAAAAAAAA";
        var saltBytes = Convert.FromBase64String(salt);
        var secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        var combined = new byte[saltBytes.Length + secretBytes.Length];
        Buffer.BlockCopy(saltBytes, 0, combined, 0, saltBytes.Length);
        Buffer.BlockCopy(secretBytes, 0, combined, saltBytes.Length, secretBytes.Length);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(combined);
        var apiKeyHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Create admin user directly using the blob storage service
        var blobStorage = scope.ServiceProvider.GetRequiredService<Server.Services.IBlobStorageService>();
        var usersJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Users = new[]
            {
                new
                {
                    Id = TestAdminUserId,
                    Name = "Test Admin",
                    Email = "admin@test.com",
                    Role = Shared.Models.UserRole.Admin,
                    OwnedFeeds = Array.Empty<string>(),
                    ApiKeyHash = apiKeyHash,
                    ApiKeySalt = salt,
                    CreatedAt = DateTime.UtcNow
                }
            }
        });
        await blobStorage.SaveUsersConfigAsync(usersJson);

        // Reload users to pick up the seeded user
        await userService.LoadUsersAsync();
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
