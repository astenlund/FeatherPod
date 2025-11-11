using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/xml");

        var content = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(content);
        doc.Root!.Name.LocalName.Should().Be("rss");
        doc.Root.Element("channel")!.Element("title")!.Value.Should().Be("Test Podcast");
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
        postResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdEpisode = await postResponse.Content.ReadAsStringAsync();
        createdEpisode.Should().Contain("Test Episode");

        // Act - Get feed
        var feedResponse = await _client.GetAsync("/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);

        // Assert - Episode appears in feed
        var items = doc.Root!.Element("channel")!.Elements("item").ToList();
        items.Should().HaveCount(1);
        items[0].Element("title")!.Value.Should().Be("Test Episode");
        items[0].Element("description")!.Value.Should().Be("This is a test episode");
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
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("audio/mpeg");
        var downloadedContent = await response.Content.ReadAsByteArrayAsync();
        downloadedContent.Should().BeEquivalentTo(audioData);
    }

    [AzuriteFact]
    public async Task GetAudio_ShouldReturn404_WhenFileNotFound()
    {
        // Act
        var response = await _client.GetAsync("/audio/nonexistent.mp3");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it's gone from feed
        var feedResponse = await _client.GetAsync("/feed.xml");
        var feedContent = await feedResponse.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(feedContent);
        var items = doc.Root!.Element("channel")!.Elements("item").ToList();
        items.Should().BeEmpty();
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
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var contentText = await response.Content.ReadAsStringAsync();
        contentText.Should().Contain("Episode 1");
        contentText.Should().Contain("Episode 2");
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
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

}
