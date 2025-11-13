using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FeatherPod.Services;

namespace FeatherPod.Tests;

[CollectionDefinition("IntegrationTests", DisableParallelization = true)]
public class IntegrationTestCollection
{
}

public class FeatherPodWebApplicationFactory : WebApplicationFactory<Program>
{
    public string AudioContainerName { get; }
    public string MetadataContainerName { get; }
    public string ApiKey { get; } = "test-api-key-12345";

    public FeatherPodWebApplicationFactory()
    {
        // Use unique container names for test isolation
        var testId = Guid.NewGuid().ToString("N").Substring(0, 12);
        AudioContainerName = $"audio-test-{testId}";
        MetadataContainerName = $"metadata-test-{testId}";
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string>
            {
                // Azure Blob Storage configuration for Azurite
                ["Azure:ConnectionString"] = "UseDevelopmentStorage=true",
                ["Azure:AudioContainerName"] = AudioContainerName,
                ["Azure:MetadataContainerName"] = MetadataContainerName,

                // Podcast configuration
                ["Podcast:Title"] = "Test Podcast",
                ["Podcast:Description"] = "Test Description",
                ["Podcast:Author"] = "Test Author",
                ["Podcast:Email"] = "test@example.com",
                ["Podcast:Language"] = "en-us",
                ["Podcast:Category"] = "Technology",
                ["Podcast:ImageUrl"] = "",
                ["Podcast:BaseUrl"] = "http://localhost:5000",

                // API Key for authenticated endpoints
                ["ApiKey"] = ApiKey
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

        return base.CreateHost(builder);
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

[Collection("IntegrationTests")]
public class IntegrationTests : IDisposable
{
    private readonly FeatherPodWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public IntegrationTests()
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
    public async Task GetFeed_ShouldReturnValidXml_WhenNoEpisodes()
    {
        // Act
        var response = await _client.GetAsync("/feed.xml");

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
        var audioContent = "fake audio data";
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(audioContent));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test-episode.mp3");
        content.Add(new StringContent("Test Episode"), "title");
        content.Add(new StringContent("This is a test episode"), "description");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", _factory.ApiKey);

        // Act - Add episode
        var postResponse = await _client.SendAsync(request);

        // Assert - Episode created
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var createdEpisode = await postResponse.Content.ReadAsStringAsync();
        Assert.Contains("Test Episode", createdEpisode);

        // Act - Get feed
        var feedResponse = await _client.GetAsync("/feed.xml");
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
        // Arrange - Create an episode via API
        var audioData = new byte[] { 0x49, 0x44, 0x33 }; // Fake MP3 header
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audioData);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test-audio.mp3");
        content.Add(new StringContent("Test Audio"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", _factory.ApiKey);

        var postResponse = await _client.SendAsync(request);
        postResponse.EnsureSuccessStatusCode();

        // Act
        var response = await _client.GetAsync("/audio/test-audio.mp3");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType!.MediaType);
        var downloadedContent = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(audioData, downloadedContent);
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldReturn404_WhenFileNotFound()
    {
        // Act
        var response = await _client.GetAsync("/audio/nonexistent.mp3");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [AzuriteFact]
    public async Task DeleteEpisode_ShouldRemoveEpisodeFromFeed()
    {
        // Arrange - Create an episode
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "deleteme.mp3");
        content.Add(new StringContent("Delete Me"), "title");

        var postRequest = new HttpRequestMessage(HttpMethod.Post, "/api/episodes");
        postRequest.Content = content;
        postRequest.Headers.Add("X-API-Key", _factory.ApiKey);

        var postResponse = await _client.SendAsync(postRequest);
        postResponse.EnsureSuccessStatusCode();

        var createdContent = await postResponse.Content.ReadAsStringAsync();
        var idMatch = System.Text.RegularExpressions.Regex.Match(createdContent, @"""id"":""([^""]+)""");
        var episodeId = idMatch.Groups[1].Value;

        // Act - Delete episode
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/episodes/{episodeId}");
        deleteRequest.Headers.Add("X-API-Key", _factory.ApiKey);
        var deleteResponse = await _client.SendAsync(deleteRequest);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Verify it's gone from feed
        var feedResponse = await _client.GetAsync("/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);
        var items = doc.Root!.Element("channel")!.Elements("item").ToList();
        Assert.Empty(items);
    }

    [AzuriteFact]
    public async Task GetEpisodes_ShouldReturnAllEpisodes()
    {
        // Arrange - Add two episodes
        for (int i = 1; i <= 2; i++)
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"audio {i}"));
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
            content.Add(fileContent, "file", $"episode{i}.mp3");
            content.Add(new StringContent($"Episode {i}"), "title");

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/episodes");
            request.Content = content;
            request.Headers.Add("X-API-Key", _factory.ApiKey);

            await _client.SendAsync(request);
        }

        // Act
        var response = await _client.GetAsync("/api/episodes");

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
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test.mp3");
        content.Add(new StringContent("Test"), "title");

        // Act - Post without API key
        var response = await _client.PostAsync("/api/episodes", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [AzuriteFact]
    public async Task PostEpisode_WithInvalidApiKey_ShouldReturn401()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("audio"));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/mpeg");
        content.Add(fileContent, "file", "test.mp3");
        content.Add(new StringContent("Test"), "title");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/episodes");
        request.Content = content;
        request.Headers.Add("X-API-Key", "wrong-key");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}
