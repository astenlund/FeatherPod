using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Tests;

[CollectionDefinition("IntegrationTests", DisableParallelization = true)]
public class IntegrationTestCollection
{
}

[Collection("IntegrationTests")]
public sealed class IntegrationTests : IDisposable
{
    private readonly FeatherPodWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private const string TestFeedId = "test-feed";

    public IntegrationTests()
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

    [AzuriteFact]
    public async Task GetFeed_ShouldReturnValidXml_WhenNoEpisodes()
    {
        // Arrange
        await CreateTestFeedAsync();

        // Act
        var response = await _client.GetAsync($"/{TestFeedId}/feed.xml");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType!.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(content);
        Assert.Equal("rss", doc.Root!.Name.LocalName);
        Assert.Equal("Test Podcast", doc.Root.Element("channel")!.Element("title")!.Value);
    }

    [AzuriteFact]
    public async Task PostEpisode_ShouldAddEpisodeAndAppearInFeed()
    {
        // Arrange
        await CreateTestFeedAsync();

        var audioContent = "fake audio data";
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(audioContent));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test-episode.mp3");
        content.Add(new StringContent("Test Episode"), "title");
        content.Add(new StringContent("This is a test episode"), "description");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act - Add episode
        var postResponse = await _client.SendAsync(request);

        // Assert - Episode created
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var createdEpisode = await postResponse.Content.ReadAsStringAsync();
        Assert.Contains("Test Episode", createdEpisode);

        // Act - Get feed
        var feedResponse = await _client.GetAsync($"/{TestFeedId}/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);

        // Assert - Episode appears in feed
        var items = doc.Root!.Element("channel")!.Elements("item").ToList();
        Assert.Single(items);
        Assert.Equal("Test Episode", items[0].Element("title")!.Value);
        Assert.Equal("This is a test episode", items[0].Element("description")!.Value);
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldServeAudioFile()
    {
        // Arrange
        await CreateTestFeedAsync();

        var audioData = "ID3"u8.ToArray(); // Fake MP3 header
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioData);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test-audio.mp3");
        content.Add(new StringContent("Test Audio"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        var postResponse = await _client.SendAsync(request);
        postResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.GetAsync($"/{TestFeedId}/audio/test-audio.mp3");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType!.MediaType);
        var downloadedContent = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(audioData, downloadedContent);
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldReturn404_WhenFileNotFound()
    {
        // Arrange
        await CreateTestFeedAsync();

        // Act
        var response = await _client.GetAsync($"/{TestFeedId}/audio/nonexistent.mp3");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldReturn206_ForStandardRangeRequest()
    {
        // Arrange
        await CreateTestFeedAsync();
        var audioData = new byte[1000];
        for (var i = 0; i < audioData.Length; i++) audioData[i] = (byte)(i % 256);
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioData);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "range-test.mp3");
        content.Add(new StringContent("Range Test"), "title");
        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(postRequest);

        // Act - Request bytes 100-199
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{TestFeedId}/audio/range-test.mp3");
        request.Headers.Add("Range", "bytes=100-199");
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 100-199/1000", response.Content.Headers.ContentRange!.ToString());
        var downloadedContent = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, downloadedContent.Length);
        Assert.Equal(audioData[100..200], downloadedContent);
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldReturn206_ForOpenEndedRangeRequest()
    {
        // Arrange
        await CreateTestFeedAsync();
        var audioData = new byte[500];
        for (var i = 0; i < audioData.Length; i++) audioData[i] = (byte)(i % 256);
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioData);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "open-range.mp3");
        content.Add(new StringContent("Open Range Test"), "title");
        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(postRequest);

        // Act - Request bytes 400 to end
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{TestFeedId}/audio/open-range.mp3");
        request.Headers.Add("Range", "bytes=400-");
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 400-499/500", response.Content.Headers.ContentRange!.ToString());
        var downloadedContent = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, downloadedContent.Length);
        Assert.Equal(audioData[400..500], downloadedContent);
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldReturn206_ForSuffixRangeRequest()
    {
        // Arrange
        await CreateTestFeedAsync();
        var audioData = new byte[500];
        for (var i = 0; i < audioData.Length; i++) audioData[i] = (byte)(i % 256);
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioData);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "suffix-range.mp3");
        content.Add(new StringContent("Suffix Range Test"), "title");
        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(postRequest);

        // Act - Request last 50 bytes (bytes=-50)
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{TestFeedId}/audio/suffix-range.mp3");
        request.Headers.Add("Range", "bytes=-50");
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 450-499/500", response.Content.Headers.ContentRange!.ToString());
        var downloadedContent = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(50, downloadedContent.Length);
        Assert.Equal(audioData[450..500], downloadedContent);
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldReturn416_ForUnsatisfiableRange()
    {
        // Arrange
        await CreateTestFeedAsync();
        var audioData = new byte[100];

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioData);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "small-file.mp3");
        content.Add(new StringContent("Small File"), "title");

        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(postRequest);

        // Act - Request range starting beyond file size
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{TestFeedId}/audio/small-file.mp3");
        request.Headers.Add("Range", "bytes=200-300");
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */100", response.Content.Headers.ContentRange!.ToString());
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldReturn416_ForMalformedRange()
    {
        // Arrange
        await CreateTestFeedAsync();
        var audioData = new byte[100];

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioData);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "malformed-test.mp3");
        content.Add(new StringContent("Malformed Test"), "title");

        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(postRequest);

        // Act - Send malformed range header (use TryAddWithoutValidation to bypass client-side validation)
        var request = new HttpRequestMessage(HttpMethod.Get, $"/{TestFeedId}/audio/malformed-test.mp3");
        request.Headers.TryAddWithoutValidation("Range", "bytes=abc-def");
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [AzuriteFact]
    public async Task DeleteEpisode_ShouldRemoveEpisodeFromFeed()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "deleteme.mp3");
        content.Add(new StringContent("Delete Me"), "title");

        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        var postResponse = await _client.SendAsync(postRequest);
        postResponse.EnsureSuccessStatusCode();

        var createdContent = await postResponse.Content.ReadAsStringAsync();
        var idMatch = System.Text.RegularExpressions.Regex.Match(createdContent, @"""id"":""([^""]+)""");
        var episodeId = idMatch.Groups[1].Value;

        // Act - Delete episode
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/feeds/{TestFeedId}/episodes/{episodeId}");
        deleteRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var deleteResponse = await _client.SendAsync(deleteRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // Verify it's gone from feed
        var feedResponse = await _client.GetAsync($"/{TestFeedId}/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);
        var items = doc.Root!.Element("channel")!.Elements("item").ToList();
        Assert.Empty(items);
    }

    [AzuriteFact]
    public async Task GetEpisodes_ShouldReturnAllEpisodes()
    {
        // Arrange
        await CreateTestFeedAsync();

        // Add two episodes
        for (var i = 1; i <= 2; i++)
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"audio {i}"));
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
            content.Add(fileContent, "file", $"episode{i}.mp3");
            content.Add(new StringContent($"Episode {i}"), "title");

            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
            request.Content = content;
            request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

            await _client.SendAsync(request);
        }

        // Act
        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/feeds/{TestFeedId}/episodes");
        getRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var response = await _client.SendAsync(getRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentText = await response.Content.ReadAsStringAsync();
        Assert.Contains("Episode 1", contentText);
        Assert.Contains("Episode 2", contentText);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithoutApiKey_ShouldReturn401()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test.mp3");
        content.Add(new StringContent("Test"), "title");

        // Act - Post without API key
        var response = await _client.PostAsync($"/api/feeds/{TestFeedId}/episodes", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithInvalidApiKey_ShouldReturn401()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test.mp3");
        content.Add(new StringContent("Test"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", "wrong-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ============================================================================
    // FEED MANAGEMENT TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task CreateFeed_ShouldCreateNewFeed()
    {
        // Arrange
        var feedJson = """
        {
            "id": "my-feed",
            "title": "My Podcast",
            "description": "A great podcast",
            "author": "John Doe",
            "email": "john@example.com",
            "language": "en",
            "category": "Technology"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds");
        request.Content = new StringContent(feedJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("my-feed", content);
        Assert.Contains("My Podcast", content);

        // Verify feed can be retrieved
        var getResponse = await _client.GetAsync("/api/feeds/my-feed");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [AzuriteFact]
    public async Task GetFeeds_ShouldReturnAllFeeds()
    {
        // Arrange
        await CreateTestFeedAsync("feed1");
        await CreateTestFeedAsync("feed2");

        // Act
        var response = await _client.GetAsync("/api/feeds");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("feed1", content);
        Assert.Contains("feed2", content);
    }

    [AzuriteFact]
    public async Task RenameFeed_ShouldRenameExistingFeed()
    {
        // Arrange
        await CreateTestFeedAsync("old-name");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/old-name/rename?newId=new-name");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify old feed is gone
        var oldResponse = await _client.GetAsync("/api/feeds/old-name");
        Assert.Equal(HttpStatusCode.NotFound, oldResponse.StatusCode);

        // Verify new feed exists
        var newResponse = await _client.GetAsync("/api/feeds/new-name");
        Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode);
    }

    [AzuriteFact]
    public async Task DeleteFeed_ShouldDeleteFeedAndEpisodes()
    {
        // Arrange
        await CreateTestFeedAsync("delete-me");

        // Add an episode to the feed
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "episode.mp3");
        content.Add(new StringContent("Episode"), "title");

        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/delete-me/episodes");
        uploadRequest.Content = content;
        uploadRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(uploadRequest);

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/feeds/delete-me");
        deleteRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(deleteRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify feed is gone
        var getResponse = await _client.GetAsync("/api/feeds/delete-me");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);

        // Verify feed's RSS is gone
        var rssResponse = await _client.GetAsync("/delete-me/feed.xml");
        Assert.Equal(HttpStatusCode.NotFound, rssResponse.StatusCode);
    }

    // ============================================================================
    // MULTI-FEED ISOLATION TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task Episodes_ShouldBeIsolatedBetweenFeeds()
    {
        // Arrange
        await CreateTestFeedAsync("feed-a");
        await CreateTestFeedAsync("feed-b");

        // Add episode to feed-a
        var contentA = new MultipartFormDataContent();
        var fileContentA = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio a"));
        fileContentA.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        contentA.Add(fileContentA, "file", "episode-a.mp3");
        contentA.Add(new StringContent("Episode A"), "title");

        var requestA = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/feed-a/episodes");
        requestA.Content = contentA;
        requestA.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(requestA);

        // Add episode to feed-b
        var contentB = new MultipartFormDataContent();
        var fileContentB = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio b"));
        fileContentB.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        contentB.Add(fileContentB, "file", "episode-b.mp3");
        contentB.Add(new StringContent("Episode B"), "title");

        var requestB = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/feed-b/episodes");
        requestB.Content = contentB;
        requestB.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(requestB);

        // Act - Get episodes from feed-a
        var getRequestA = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/feed-a/episodes");
        getRequestA.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var responseA = await _client.SendAsync(getRequestA);
        var jsonA = await responseA.Content.ReadAsStringAsync();

        // Act - Get episodes from feed-b
        var getRequestB = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/feed-b/episodes");
        getRequestB.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var responseB = await _client.SendAsync(getRequestB);
        var jsonB = await responseB.Content.ReadAsStringAsync();

        // Assert - Episodes are isolated
        Assert.Contains("Episode A", jsonA);
        Assert.DoesNotContain("Episode B", jsonA);

        Assert.Contains("Episode B", jsonB);
        Assert.DoesNotContain("Episode A", jsonB);
    }

    [AzuriteFact]
    public async Task MoveEpisode_ShouldMoveEpisodeBetweenFeeds()
    {
        // Arrange
        await CreateTestFeedAsync("source-feed");
        await CreateTestFeedAsync("target-feed");

        // Add episode to source feed
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "movable.mp3");
        content.Add(new StringContent("Movable Episode"), "title");

        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/source-feed/episodes");
        uploadRequest.Content = content;
        uploadRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var uploadResponse = await _client.SendAsync(uploadRequest);
        var uploadJson = await uploadResponse.Content.ReadAsStringAsync();
        var episodeIdMatch = System.Text.RegularExpressions.Regex.Match(uploadJson, @"""id"":""([^""]+)""");
        var episodeId = episodeIdMatch.Groups[1].Value;

        // Act - Move episode
        var moveJson = """{"targetFeedId": "target-feed"}""";
        var moveRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/source-feed/episodes/{episodeId}/move");
        moveRequest.Content = new StringContent(moveJson, System.Text.Encoding.UTF8, "application/json");
        moveRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var moveResponse = await _client.SendAsync(moveRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, moveResponse.StatusCode);

        // Verify episode is gone from source
        var getSourceRequest = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/source-feed/episodes");
        getSourceRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var sourceResponse = await _client.SendAsync(getSourceRequest);
        var sourceJson = await sourceResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Movable Episode", sourceJson);

        // Verify episode is in target
        var getTargetRequest = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/target-feed/episodes");
        getTargetRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var targetResponse = await _client.SendAsync(getTargetRequest);
        var targetJson = await targetResponse.Content.ReadAsStringAsync();
        Assert.Contains("Movable Episode", targetJson);
    }

    [AzuriteFact]
    public async Task CopyEpisode_ShouldCopyEpisodeBetweenFeeds()
    {
        // Arrange
        await CreateTestFeedAsync("source-feed");
        await CreateTestFeedAsync("target-feed");

        // Add episode to source feed
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "copyable.mp3");
        content.Add(new StringContent("Copyable Episode"), "title");

        var uploadRequest = new HttpRequestMessage(HttpMethod.Post, "/api/feeds/source-feed/episodes");
        uploadRequest.Content = content;
        uploadRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var uploadResponse = await _client.SendAsync(uploadRequest);
        var uploadJson = await uploadResponse.Content.ReadAsStringAsync();
        var episodeIdMatch = System.Text.RegularExpressions.Regex.Match(uploadJson, @"""id"":""([^""]+)""");
        var episodeId = episodeIdMatch.Groups[1].Value;

        // Act - Copy episode
        var copyJson = """{"targetFeedId": "target-feed"}""";
        var copyRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/source-feed/episodes/{episodeId}/copy");
        copyRequest.Content = new StringContent(copyJson, System.Text.Encoding.UTF8, "application/json");
        copyRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var copyResponse = await _client.SendAsync(copyRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, copyResponse.StatusCode);

        // Verify episode is still in source
        var getSourceRequest = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/source-feed/episodes");
        getSourceRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var sourceResponse = await _client.SendAsync(getSourceRequest);
        var sourceJson = await sourceResponse.Content.ReadAsStringAsync();
        Assert.Contains("Copyable Episode", sourceJson);

        // Verify episode is also in target
        var getTargetRequest = new HttpRequestMessage(HttpMethod.Get, "/api/feeds/target-feed/episodes");
        getTargetRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var targetResponse = await _client.SendAsync(getTargetRequest);
        var targetJson = await targetResponse.Content.ReadAsStringAsync();
        Assert.Contains("Copyable Episode", targetJson);
    }

    [AzuriteFact]
    public async Task Episode_WithBothDescriptionAndSummary_ShouldUseCorrectFieldsInRSS()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test.mp3");
        content.Add(new StringContent("Test Episode"), "title");
        content.Add(new StringContent("This is the full RSS description with lots of detail"), "description");
        content.Add(new StringContent("Short iTunes summary"), "summary");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Verify RSS feed uses description for <description> and summary for <itunes:summary>
        var feedResponse = await _client.GetAsync($"/{TestFeedId}/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);
        var ns = XNamespace.Get("http://www.itunes.com/dtds/podcast-1.0.dtd");

        var item = doc.Root!.Element("channel")!.Element("item")!;
        var description = item.Element("description")!.Value;
        var itunesSummary = item.Element(ns + "summary")!.Value;

        Assert.Equal("This is the full RSS description with lots of detail", description);
        Assert.Equal("Short iTunes summary", itunesSummary);
    }

    [AzuriteFact]
    public async Task Episode_WithOnlyDescription_ShouldFallbackToDescriptionForSummary()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test.mp3");
        content.Add(new StringContent("Test Episode"), "title");
        content.Add(new StringContent("Single description for both"), "description");
        // No summary provided

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Verify RSS feed uses description for both fields
        var feedResponse = await _client.GetAsync($"/{TestFeedId}/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);
        var ns = XNamespace.Get("http://www.itunes.com/dtds/podcast-1.0.dtd");

        var item = doc.Root!.Element("channel")!.Element("item")!;
        var description = item.Element("description")!.Value;
        var itunesSummary = item.Element(ns + "summary")!.Value;

        Assert.Equal("Single description for both", description);
        Assert.Equal("Single description for both", itunesSummary);
    }

    [AzuriteFact]
    public async Task Episode_WithNoDescriptionOrSummary_ShouldHaveEmptyFields()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test.mp3");
        content.Add(new StringContent("Test Episode"), "title");
        // No description or summary provided

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Verify RSS feed has empty description and summary
        var feedResponse = await _client.GetAsync($"/{TestFeedId}/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);
        var ns = XNamespace.Get("http://www.itunes.com/dtds/podcast-1.0.dtd");

        var item = doc.Root!.Element("channel")!.Element("item")!;
        var description = item.Element("description")!.Value;
        var itunesSummary = item.Element(ns + "summary")!.Value;

        Assert.Equal(string.Empty, description);
        Assert.Equal(string.Empty, itunesSummary);
    }

    // ============================================================================
    // AUDIO NORMALIZATION TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task PostEpisode_WithNormalizeFalse_ShouldUploadWithoutNormalization()
    {
        // Arrange
        await CreateTestFeedAsync();

        const string audioContent = "fake audio data";
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(audioContent));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "no-normalize.mp3");
        content.Add(new StringContent("No Normalize Episode"), "title");

        // Use ?normalize=false explicitly
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes?normalize=false");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert - Should succeed without normalization
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var createdEpisode = await response.Content.ReadAsStringAsync();
        Assert.Contains("No Normalize Episode", createdEpisode);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithNormalizeTrue_ShouldReturn202WithJobStatus()
    {
        // Arrange
        await CreateTestFeedAsync();

        const string audioContent = "fake audio data";
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(audioContent));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "normalize-test.mp3");
        content.Add(new StringContent("Normalize Test Episode"), "title");

        // Use ?normalize=true - queues job for async processing
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes?normalize=true");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert - Async normalization returns 202 Accepted with job status
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("jobId", responseContent);
        Assert.Contains("feedId", responseContent);
        Assert.Contains("Queued", responseContent);
        Assert.Contains("episodeId", responseContent);
    }

    // ============================================================================
    // PUSH PAGE TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task GetPushPage_ShouldReturnHtml_WhenFeedExists()
    {
        // Arrange
        await CreateTestFeedAsync();

        // Act
        var response = await _client.GetAsync($"/{TestFeedId}/push");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("<!DOCTYPE html>", content);
        Assert.Contains($"/{TestFeedId}/icon.png", content);
        Assert.Contains($"const FEED_ID = '{TestFeedId}'", content);
        Assert.Contains("Push to Test Podcast", content);
    }

    [AzuriteFact]
    public async Task GetPushPage_ShouldReturn404_WhenFeedNotFound()
    {
        // Act
        var response = await _client.GetAsync("/nonexistent-feed/push");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [AzuriteFact]
    public async Task GetPushPage_ShouldReturn400_WhenFeedIdInvalid()
    {
        // Act - Feed ID that's too long (>64 chars) is invalid
        var tooLongFeedId = new string('a', 65);
        var response = await _client.GetAsync($"/{tooLongFeedId}/push");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task GetPushPage_ShouldEscapeFeedTitle()
    {
        // Arrange - Create feed with XSS attempt in title
        var feedJson = """
        {
            "id": "xss-test",
            "title": "Test <script>alert('xss')</script>",
            "description": "Test",
            "author": "Test"
        }
        """;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/feeds");
        request.Content = new StringContent(feedJson, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(request);

        // Act
        var response = await _client.GetAsync("/xss-test/push");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.DoesNotContain("<script>alert", content);
        Assert.Contains("&lt;script&gt;", content);
    }

    // ============================================================================
    // EPISODE ID PARAMETER TESTS
    // ============================================================================

    [AzuriteFact]
    public async Task PostEpisode_WithEpisodeIdParameter_ShouldUseProvidedId()
    {
        // Arrange
        await CreateTestFeedAsync();

        var customEpisodeId = "custom123abc";
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio data"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test-episode.mp3");
        content.Add(new StringContent("Test Episode"), "title");
        content.Add(new StringContent(customEpisodeId), "episodeId");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains($"\"id\":\"{customEpisodeId}\"", responseContent);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithEpisodeIdParameter_ShouldReplaceExistingEpisode()
    {
        // Arrange
        await CreateTestFeedAsync();

        var sharedEpisodeId = "shared456def";

        // First upload
        var content1 = new MultipartFormDataContent();
        var fileContent1 = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("original audio"));
        fileContent1.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content1.Add(fileContent1, "file", "episode.mp3");
        content1.Add(new StringContent("First Upload"), "title");
        content1.Add(new StringContent(sharedEpisodeId), "episodeId");

        var request1 = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request1.Content = content1;
        request1.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(request1);

        // Second upload with same episodeId but different file size (simulates re-upload after normalization)
        var content2 = new MultipartFormDataContent();
        var fileContent2 = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("normalized")); // Different size
        fileContent2.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content2.Add(fileContent2, "file", "episode.mp3");
        content2.Add(new StringContent("Second Upload"), "title");
        content2.Add(new StringContent(sharedEpisodeId), "episodeId");

        var request2 = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request2.Content = content2;
        request2.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response2 = await _client.SendAsync(request2);

        // Get episodes to verify only one exists
        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/feeds/{TestFeedId}/episodes");
        getRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var getResponse = await _client.SendAsync(getRequest);
        var episodesJson = await getResponse.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.Created, response2.StatusCode);

        // Should only have one episode (the second upload replaced the first)
        var episodeCount = System.Text.RegularExpressions.Regex.Matches(episodesJson, "\"id\":").Count;
        Assert.Equal(1, episodeCount);
        Assert.Contains("Second Upload", episodesJson);
        Assert.DoesNotContain("First Upload", episodesJson);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithoutTitle_ShouldGenerateTitleViaAi()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "My_Cool_Episode__Part_One.mp3");
        // No title provided - FakeAiService returns ParseTitleFromFilename output

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // Should be parsed: underscores to spaces, double underscore to colon
        Assert.Contains("My Cool Episode: Part One", responseContent);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithExplicitTitle_ShouldPreserveTitle()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "Some_Random_Filename.mp3");
        content.Add(new StringContent("My Custom Title"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        // Assert - explicit title should be preserved, not replaced by AI/filename parsing
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("My Custom Title", responseContent);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithNormalize_WithoutTitle_ShouldUseAiTitle()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "auto_title_test.mp3");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes?normalize=true");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert - job should be queued with AI-generated title (FakeAiService uses ParseTitleFromFilename)
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("jobId", responseContent);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithNormalize_WithTitle_ShouldPreserveExplicitTitle()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "explicit_title_test.mp3");
        content.Add(new StringContent("Explicit Normalize Title"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes?normalize=true");
        request.Content = content;
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert - job should be queued with the explicit title
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("jobId", responseContent);
    }

    [AzuriteFact]
    public async Task PatchEpisode_ShouldUpdateTitle()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "rename-me.mp3");
        content.Add(new StringContent("Original Title"), "title");

        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);

        var postResponse = await _client.SendAsync(postRequest);
        postResponse.EnsureSuccessStatusCode();

        var createdContent = await postResponse.Content.ReadAsStringAsync();
        var idMatch = System.Text.RegularExpressions.Regex.Match(createdContent, @"""id"":""([^""]+)""");
        var episodeId = idMatch.Groups[1].Value;

        // Act
        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/feeds/{TestFeedId}/episodes/{episodeId}");
        patchRequest.Content = new StringContent("""{"title": "Updated Title"}""", System.Text.Encoding.UTF8, "application/json");
        patchRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var patchResponse = await _client.SendAsync(patchRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var patchContent = await patchResponse.Content.ReadAsStringAsync();
        Assert.Contains("Updated Title", patchContent);

        // Verify RSS feed reflects the new title
        var feedResponse = await _client.GetAsync($"/{TestFeedId}/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);
        var items = doc.Root!.Element("channel")!.Elements("item").ToList();
        Assert.Single(items);
        Assert.Equal("Updated Title", items[0].Element("title")!.Value);
    }

    [AzuriteFact]
    public async Task PatchEpisode_ShouldReturn404_WhenEpisodeNotFound()
    {
        // Arrange
        await CreateTestFeedAsync();

        // Act
        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/feeds/{TestFeedId}/episodes/nonexistent");
        patchRequest.Content = new StringContent("""{"title": "New Title"}""", System.Text.Encoding.UTF8, "application/json");
        patchRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var response = await _client.SendAsync(patchRequest);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [AzuriteFact]
    public async Task PatchEpisode_ShouldReturn400_WhenTitleEmpty()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "episode.mp3");
        content.Add(new StringContent("Test"), "title");

        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        await _client.SendAsync(postRequest);

        // Act
        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/feeds/{TestFeedId}/episodes/someId");
        patchRequest.Content = new StringContent("""{"title": "  "}""", System.Text.Encoding.UTF8, "application/json");
        patchRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var response = await _client.SendAsync(patchRequest);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task SuggestTitle_ShouldReturnParsedTitle_ViaFakeAiService()
    {
        // Arrange
        await CreateTestFeedAsync();

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "my_test_episode_file.mp3");
        content.Add(new StringContent("My Title"), "title");

        var postRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var postResponse = await _client.SendAsync(postRequest);
        postResponse.EnsureSuccessStatusCode();

        var createdContent = await postResponse.Content.ReadAsStringAsync();
        var idMatch = System.Text.RegularExpressions.Regex.Match(createdContent, @"""id"":""([^""]+)""");
        var episodeId = idMatch.Groups[1].Value;

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes/{episodeId}/suggest-title");
        request.Headers.Add("X-API-Key", FeatherPodWebApplicationFactory.ApiKey);
        var response = await _client.SendAsync(request);

        // Assert - FakeAiService returns ParseTitleFromFilename result
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseContent = await response.Content.ReadAsStringAsync();
        Assert.Contains("suggestedTitle", responseContent);
        Assert.Contains("my test episode file", responseContent);
    }

    [AzuriteFact]
    public async Task SuggestTitle_ShouldReturn401_WithoutApiKey()
    {
        // Arrange
        await CreateTestFeedAsync();

        // Act
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/feeds/{TestFeedId}/episodes/some-id/suggest-title");
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

internal class FeatherPodWebApplicationFactory : WebApplicationFactory<FeatherPod.Server.ServerAssemblyMarker>
{
    // API key in new fp_ format: fp_{userId}_{secret}
    // The secret is 22 chars base64url (128 bits)
    public const string ApiKey = "fp_test-admin_AAAAAAAAAAAAAAAAAAAAAA";
    public const string InternalKey = "test-internal-key";
    private const string TestAdminUserId = "test-admin";

    private readonly string ContainerName;

    public FeatherPodWebApplicationFactory()
    {
        // Use unique container name for test isolation
        var testId = Guid.NewGuid().ToString("N")[..12];
        ContainerName = $"featherpod-test-{testId}";
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string>
            {
                // Azure Blob Storage configuration for Azurite
                ["Azure:ConnectionString"] = "UseDevelopmentStorage=true",
                ["Azure:ContainerName"] = ContainerName,

                // Podcast configuration (BaseUrl only)
                ["Podcast:BaseUrl"] = "http://localhost:5000",

                // Internal key for service-to-service endpoints
                ["Internal:Key"] = InternalKey,

                // Push page defaults for tests (push mode enables instant SSE delivery via channel)
                ["PushPage:ProgressMode"] = "push",
                ["PushPage:ProgressIntervalMs"] = "250",
                ["PushPage:PollIntervalMs"] = "500",

                // Disable AI features in tests (override appsettings.Development.json)
                ["AzureOpenAI:Endpoint"] = "",
                ["AzureOpenAI:Deployment"] = ""
            }!);
        });

        // Suppress warnings from test dummy files
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddConsole();
            logging.AddFilter("FeatherPod.Services.EpisodeService", LogLevel.Error);
            logging.SetMinimumLevel(LogLevel.Information);
        });

        // Create the host
        var host = base.CreateHost(builder);

        // Pre-seed the admin user for tests
        SeedTestUserAsync(host.Services).GetAwaiter().GetResult();

        return host;
    }

    private static async Task SeedTestUserAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

        // Check if user already exists
        var existingUser = await userService.GetUserByIdAsync(TestAdminUserId);
        if (existingUser != null)
        {
            return;
        }

        // Create the test admin user with a known API key
        // We need to manually compute the hash to match our hardcoded API key
        var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();

        // Extract secret from fp_{userId}_{secret}
        var secret = ApiKey[(ApiKey.IndexOf('_', 3) + 1)..];

        // Generate a deterministic salt for testing (all zeros for simplicity)
        var salt = Convert.ToBase64String(new byte[16]);

        // Compute hash: SHA256(salt + secret)
        var saltBytes = Convert.FromBase64String(salt);
        var secretBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        var combined = new byte[saltBytes.Length + secretBytes.Length];
        Buffer.BlockCopy(saltBytes, 0, combined, 0, saltBytes.Length);
        Buffer.BlockCopy(secretBytes, 0, combined, saltBytes.Length, secretBytes.Length);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(combined);
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Create users.json with the test admin user
        var usersMetadata = new UsersMetadata
        {
            Users =
            [
                new User
                {
                    Id = TestAdminUserId,
                    Name = "Test Admin",
                    Email = "test@example.com",
                    Role = UserRole.Admin,
                    ApiKeyHash = hash,
                    ApiKeySalt = salt,
                    OwnedFeeds = [],
                    CreatedAt = DateTime.UtcNow
                }
            ]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(usersMetadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await blobStorage.SaveUsersConfigAsync(json);

        // Reload user service to pick up the new user
        await userService.LoadUsersAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Give services time to stop
            Thread.Sleep(200);

            // Blob containers will be cleaned up automatically by Azurite
            // or can be left for debugging if needed
        }
        base.Dispose(disposing);
    }
}
