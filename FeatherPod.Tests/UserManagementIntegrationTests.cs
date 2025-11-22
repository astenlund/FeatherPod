using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Tests;

[Collection("IntegrationTests")]
public class UserManagementIntegrationTests : IDisposable
{
    private readonly UserManagementWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private const string AdminApiKey = "admin-api-key-12345";

    public UserManagementIntegrationTests()
    {
        _factory = new();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<string> CreateTestUserAsync(string userId, string role = "FeedOwner", string[]? ownedFeeds = null)
    {
        var userJson = $$"""
        {
            "id": "{{userId}}",
            "name": "{{userId}} Name",
            "email": "{{userId}}@example.com",
            "role": "{{role}}",
            "ownedFeeds": {{JsonSerializer.Serialize(ownedFeeds ?? [])}}
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users");
        request.Content = new StringContent(userJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", AdminApiKey);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(responseJson);
        return doc.GetProperty("apiKey").GetString()!;
    }

    private async Task CreateTestFeedAsync(string feedId, string apiKey)
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
        request.Headers.Add("X-API-Key", apiKey);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    // ============================================================================
    // USER CREATION TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task CreateUser_ShouldCreateNewUser_WhenAdmin()
    {
        // Arrange
        var userJson = """
        {
            "id": "newuser",
            "name": "New User",
            "email": "newuser@example.com",
            "role": "FeedOwner",
            "ownedFeeds": ["feed1", "feed2"]
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users");
        request.Content = new StringContent(userJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("newuser", content);
        Assert.Contains("apiKey", content);
    }

    [AzuriteFact]
    public async Task CreateUser_ShouldReturn401_WhenNoApiKey()
    {
        // Arrange
        var userJson = """
        {
            "id": "newuser",
            "name": "New User",
            "email": "newuser@example.com",
            "role": "Admin",
            "ownedFeeds": []
        }
        """;

        // Act
        var response = await _client.PostAsync("/api/users",
            new StringContent(userJson, System.Text.Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [AzuriteFact]
    public async Task CreateUser_ShouldReturn403_WhenFeedOwnerTriesToCreate()
    {
        // Arrange
        var feedOwnerApiKey = await CreateTestUserAsync("feedowner");

        var userJson = """
        {
            "id": "newuser",
            "name": "New User",
            "email": "newuser@example.com",
            "role": "FeedOwner",
            "ownedFeeds": []
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users");
        request.Content = new StringContent(userJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", feedOwnerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [AzuriteFact]
    public async Task CreateUser_ShouldReturn400_WhenDuplicateUserId()
    {
        // Arrange
        await CreateTestUserAsync("duplicate");

        var userJson = """
        {
            "id": "duplicate",
            "name": "Another User",
            "email": "another@example.com",
            "role": "FeedOwner",
            "ownedFeeds": []
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users");
        request.Content = new StringContent(userJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ============================================================================
    // USER LISTING TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task ListUsers_ShouldReturnAllUsers_WhenAdmin()
    {
        // Arrange
        await CreateTestUserAsync("user1");
        await CreateTestUserAsync("user2");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        request.Headers.Add("X-API-Key", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("user1", content);
        Assert.Contains("user2", content);
        Assert.Contains("admin", content); // Legacy migrated admin
    }

    [AzuriteFact]
    public async Task ListUsers_ShouldReturn403_WhenFeedOwner()
    {
        // Arrange
        var feedOwnerApiKey = await CreateTestUserAsync("feedowner");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users");
        request.Headers.Add("X-API-Key", feedOwnerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================================
    // USER DELETION TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task DeleteUser_ShouldDeleteUser_WhenAdmin()
    {
        // Arrange
        await CreateTestUserAsync("todelete");

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/users/todelete");
        request.Headers.Add("X-API-Key", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [AzuriteFact]
    public async Task DeleteUser_ShouldReturn403_WhenFeedOwner()
    {
        // Arrange
        await CreateTestUserAsync("victim");
        var feedOwnerApiKey = await CreateTestUserAsync("attacker");

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/users/victim");
        request.Headers.Add("X-API-Key", feedOwnerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================================
    // API KEY ROTATION TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task RotateKey_ShouldGenerateNewKey_WhenAdmin()
    {
        // Arrange
        var oldApiKey = await CreateTestUserAsync("testuser");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/testuser/key/regenerate");
        request.Headers.Add("X-API-Key", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(content);
        var newApiKey = doc.GetProperty("apiKey").GetString()!;
        Assert.NotEqual(oldApiKey, newApiKey);
    }

    [AzuriteFact]
    public async Task RotateKey_ShouldWork_WhenFeedOwnerRotatesOwnKey()
    {
        // Arrange
        var feedOwnerApiKey = await CreateTestUserAsync("selfrotate", "FeedOwner", []);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/selfrotate/key/regenerate");
        request.Headers.Add("X-API-Key", feedOwnerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(content);
        var newApiKey = doc.GetProperty("apiKey").GetString()!;
        Assert.NotEqual(feedOwnerApiKey, newApiKey);
    }

    [AzuriteFact]
    public async Task RotateKey_ShouldReturn403_WhenFeedOwnerRotatesOtherUserKey()
    {
        // Arrange
        await CreateTestUserAsync("victim", "FeedOwner", []);
        var attackerApiKey = await CreateTestUserAsync("attacker", "FeedOwner", []);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/victim/key/regenerate");
        request.Headers.Add("X-API-Key", attackerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ============================================================================
    // FEED OWNERSHIP GRANT/REVOKE TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task GrantFeedOwnership_ShouldGrantAccess_WhenAdmin()
    {
        // Arrange
        var feedOwnerApiKey = await CreateTestUserAsync("feedowner", "FeedOwner", []);

        var feedJson = """{"feedId": "new-feed"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/users/feedowner/feeds");
        request.Content = new StringContent(feedJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify user can now access the feed
        await CreateTestFeedAsync("new-feed", AdminApiKey);
        var feedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/new-feed");
        feedRequest.Headers.Add("X-API-Key", feedOwnerApiKey);
        var feedResponse = await _client.SendAsync(feedRequest);
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);
    }

    [AzuriteFact]
    public async Task RevokeFeedOwnership_ShouldRevokeAccess_WhenAdmin()
    {
        // Arrange
        var feedOwnerApiKey = await CreateTestUserAsync("feedowner", "FeedOwner", ["revoke-feed"]);
        await CreateTestFeedAsync("revoke-feed", AdminApiKey);

        // Verify user initially has access
        var initialRequest = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/revoke-feed");
        initialRequest.Headers.Add("X-API-Key", feedOwnerApiKey);
        var initialResponse = await _client.SendAsync(initialRequest);
        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);

        // Act - Revoke ownership
        var revokeRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/users/feedowner/feeds/revoke-feed");
        revokeRequest.Headers.Add("X-API-Key", AdminApiKey);
        var revokeResponse = await _client.SendAsync(revokeRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        // Verify user no longer has access (test with a write operation - episode upload)
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test.mp3");
        content.Add(new StringContent("Test Episode"), "title");

        var finalRequest = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/revoke-feed/episodes");
        finalRequest.Content = content;
        finalRequest.Headers.Add("X-API-Key", feedOwnerApiKey);
        var finalResponse = await _client.SendAsync(finalRequest);
        Assert.Equal(HttpStatusCode.Forbidden, finalResponse.StatusCode);
    }

    // ============================================================================
    // AUTHORIZATION TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task FeedAccess_Admin_ShouldAccessAllFeeds()
    {
        // Arrange
        await CreateTestFeedAsync("feed1", AdminApiKey);
        await CreateTestFeedAsync("feed2", AdminApiKey);

        // Act & Assert - Admin can access both feeds
        var request1 = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/feed1");
        request1.Headers.Add("X-API-Key", AdminApiKey);
        var response1 = await _client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/feed2");
        request2.Headers.Add("X-API-Key", AdminApiKey);
        var response2 = await _client.SendAsync(request2);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
    }

    [AzuriteFact]
    public async Task FeedAccess_FeedOwner_ShouldOnlyAccessOwnedFeeds()
    {
        // Arrange
        var ownerApiKey = await CreateTestUserAsync("owner", "FeedOwner", ["owned-feed"]);
        await CreateTestFeedAsync("owned-feed", AdminApiKey);
        await CreateTestFeedAsync("other-feed", AdminApiKey);

        // Act & Assert - Owner can upload to owned feed
        var ownedContent = new MultipartFormDataContent();
        var ownedFileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        ownedFileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/mpeg");
        ownedContent.Add(ownedFileContent, "file", "owned.mp3");
        ownedContent.Add(new StringContent("Owned Episode"), "title");

        var ownedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/owned-feed/episodes");
        ownedRequest.Content = ownedContent;
        ownedRequest.Headers.Add("X-API-Key", ownerApiKey);
        var ownedResponse = await _client.SendAsync(ownedRequest);
        Assert.Equal(HttpStatusCode.Created, ownedResponse.StatusCode);

        // Act & Assert - Owner cannot upload to other feed
        var otherContent = new MultipartFormDataContent();
        var otherFileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        otherFileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/mpeg");
        otherContent.Add(otherFileContent, "file", "other.mp3");
        otherContent.Add(new StringContent("Other Episode"), "title");

        var otherRequest = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/other-feed/episodes");
        otherRequest.Content = otherContent;
        otherRequest.Headers.Add("X-API-Key", ownerApiKey);
        var otherResponse = await _client.SendAsync(otherRequest);
        Assert.Equal(HttpStatusCode.Forbidden, otherResponse.StatusCode);
    }

    [AzuriteFact]
    public async Task FeedCreation_FeedOwner_ShouldReturn403()
    {
        // Arrange
        var ownerApiKey = await CreateTestUserAsync("owner");

        var feedJson = """
        {
            "id": "unauthorized-feed",
            "title": "Unauthorized",
            "description": "Should not be created",
            "author": "Test",
            "email": "test@example.com",
            "language": "en",
            "category": "Technology"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds");
        request.Content = new StringContent(feedJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", ownerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [AzuriteFact]
    public async Task EpisodeUpload_FeedOwner_ShouldWorkForOwnedFeed()
    {
        // Arrange
        var ownerApiKey = await CreateTestUserAsync("owner", "FeedOwner", ["my-feed"]);
        await CreateTestFeedAsync("my-feed", AdminApiKey);

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "episode.mp3");
        content.Add(new StringContent("Test Episode"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/my-feed/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", ownerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [AzuriteFact]
    public async Task EpisodeUpload_FeedOwner_ShouldReturn403ForOtherFeed()
    {
        // Arrange
        var ownerApiKey = await CreateTestUserAsync("owner", "FeedOwner", ["my-feed"]);
        await CreateTestFeedAsync("my-feed", AdminApiKey);
        await CreateTestFeedAsync("other-feed", AdminApiKey);

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "episode.mp3");
        content.Add(new StringContent("Test Episode"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/other-feed/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", ownerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [AzuriteFact]
    public async Task LegacyApiKey_ShouldStillWork_AsAdmin()
    {
        // The factory sets up a legacy API key that gets migrated to admin user

        // Act - Create feed with legacy key
        await CreateTestFeedAsync("legacy-feed", AdminApiKey);

        // Assert - Feed should be created
        var response = await _client.GetAsync("/api/feeds/legacy-feed");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ============================================================================
    // GET CURRENT USER (/api/users/me) TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task GetMe_ShouldReturnCurrentUser_WhenAdmin()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me");
        request.Headers.Add("X-API-Key", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(content);

        Assert.Equal("admin", doc.GetProperty("id").GetString());
        Assert.Equal("Admin", doc.GetProperty("role").GetString());
    }

    [AzuriteFact]
    public async Task GetMe_ShouldReturnCurrentUser_WhenFeedOwner()
    {
        // Arrange
        var feedOwnerApiKey = await CreateTestUserAsync("feedowner", "FeedOwner", ["feed1", "feed2"]);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me");
        request.Headers.Add("X-API-Key", feedOwnerApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(content);

        Assert.Equal("feedowner", doc.GetProperty("id").GetString());
        Assert.Equal("feedowner Name", doc.GetProperty("name").GetString());
        Assert.Equal("feedowner@example.com", doc.GetProperty("email").GetString());
        Assert.Equal("FeedOwner", doc.GetProperty("role").GetString());

        var ownedFeeds = doc.GetProperty("ownedFeeds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("feed1", ownedFeeds);
        Assert.Contains("feed2", ownedFeeds);
    }

    [AzuriteFact]
    public async Task GetMe_ShouldReturn401_WhenNoApiKey()
    {
        // Act
        var response = await _client.GetAsync("/api/users/me");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [AzuriteFact]
    public async Task GetMe_ShouldReturn401_WhenInvalidApiKey()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me");
        request.Headers.Add("X-API-Key", "invalid-api-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [AzuriteFact]
    public async Task GetMe_ShouldNotExposeApiKeyHash()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me");
        request.Headers.Add("X-API-Key", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();

        // Should not contain apiKeyHash field
        Assert.DoesNotContain("apiKeyHash", content.ToLower());
    }
}

internal class UserManagementWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string LegacyApiKey = "admin-api-key-12345";

    private readonly string ContainerName;

    public UserManagementWebApplicationFactory()
    {
        var testId = Guid.NewGuid().ToString("N")[..12];
        ContainerName = $"featherpod-test-{testId}";
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
                ["ApiKey"] = LegacyApiKey // Legacy API key for migration
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
