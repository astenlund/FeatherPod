using System.Net;
using System.Text;
using System.Text.Json;
using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class LocalFileServerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempAudioFile;
    private readonly LocalFileServer _server;

    public LocalFileServerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"LocalFileServerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _tempAudioFile = Path.Combine(_tempDir, "test-audio.mp3");
        File.WriteAllBytes(_tempAudioFile, [0xFF, 0xFB, 0x90, 0x00]);

        _server = new LocalFileServer("http://localhost");
        _server.Start();
    }

    [Fact]
    public void Start_AssignsPort()
    {
        // Assert
        Assert.True(_server.Port > 0);
        Assert.True(_server.Port < 65536);
    }

    [Fact]
    public async Task GetFiles_ReturnsEmptyArray_WhenNoFilesAdded()
    {
        // Arrange
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync($"http://127.0.0.1:{_server.Port}/api/files?token={_server.Token}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal("[]", json);
    }

    [Fact]
    public async Task AddFile_ThenGetFiles_ReturnsFileMetadata()
    {
        // Arrange
        _server.AddFile(_tempAudioFile);
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync($"http://127.0.0.1:{_server.Port}/api/files?token={_server.Token}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var array = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(1, array.GetArrayLength());

        var file = array[0];
        Assert.Equal("test-audio.mp3", file.GetProperty("name").GetString());
        Assert.Equal(4, file.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task GetFileByIndex_ReturnsFileContent()
    {
        // Arrange
        _server.AddFile(_tempAudioFile);
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync($"http://127.0.0.1:{_server.Port}/api/files/0?token={_server.Token}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("test-audio.mp3", disposition.FileName?.Trim('"'));

        var content = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xFF, 0xFB, 0x90, 0x00], content);
    }

    [Fact]
    public async Task PostFile_AddsFileToList()
    {
        // Arrange
        using var client = new HttpClient();
        var body = JsonSerializer.Serialize(new { path = _tempAudioFile });
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync($"http://127.0.0.1:{_server.Port}/api/files?token={_server.Token}", content);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var listResponse = await client.GetAsync($"http://127.0.0.1:{_server.Port}/api/files?token={_server.Token}");
        var json = await listResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("test-audio.mp3", doc.RootElement[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task InvalidToken_Returns403()
    {
        // Arrange
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync($"http://127.0.0.1:{_server.Port}/api/files?token=wrong-token");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CorsHeaders_PresentOnResponse()
    {
        // Arrange
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync($"http://127.0.0.1:{_server.Port}/api/files?token={_server.Token}");

        // Assert
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Contains("http://localhost", origins);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods));
        Assert.Contains("GET, POST, OPTIONS", methods);
    }

    [Fact]
    public async Task SseEndpoint_StreamsNewFileEvents()
    {
        // Arrange
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(
            $"http://127.0.0.1:{_server.Port}/api/events?token={_server.Token}",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Act
        _server.AddFile(_tempAudioFile);

        // Assert
        var lines = new List<string>();

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    lines.Add(line);
                }

                if (lines.Any(l => l.StartsWith("data:") && l.Contains("test-audio.mp3")))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Contains(lines, l => l == "event: new-file");
        Assert.Contains(lines, l => l.StartsWith("data:") && l.Contains("test-audio.mp3"));
    }

    [Fact]
    public async Task GetFileByIndex_Returns404_WhenIndexOutOfRange()
    {
        // Arrange
        using var client = new HttpClient();

        // Act
        var response = await client.GetAsync($"http://127.0.0.1:{_server.Port}/api/files/99?token={_server.Token}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostFile_RejectsNonAudioExtension()
    {
        // Arrange
        var txtFile = Path.Combine(_tempDir, "document.txt");
        File.WriteAllText(txtFile, "hello");
        using var client = new HttpClient();
        var body = JsonSerializer.Serialize(new { path = txtFile });
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync($"http://127.0.0.1:{_server.Port}/api/files?token={_server.Token}", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_Returns200()
    {
        // Arrange
        using var client = new HttpClient();

        // Act
        var response = await client.PostAsync($"http://127.0.0.1:{_server.Port}/api/heartbeat?token={_server.Token}", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FileUploaded_MarksFileAndRoundTrips()
    {
        // Arrange
        _server.AddFile(_tempAudioFile);
        using var client = new HttpClient();

        // Act
        var response = await client.PostAsync($"http://127.0.0.1:{_server.Port}/api/files/0/uploaded?token={_server.Token}", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = _server.GetUploadedFilePaths();
        Assert.Single(paths);
        Assert.Equal(_tempAudioFile, paths[0]);
    }

    [Fact]
    public async Task FileUploaded_Returns404_WhenIndexOutOfRange()
    {
        // Arrange
        using var client = new HttpClient();

        // Act
        var response = await client.PostAsync($"http://127.0.0.1:{_server.Port}/api/files/99/uploaded?token={_server.Token}", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void GetUploadedFilePaths_ReturnsEmpty_WhenNoUploadsConfirmed()
    {
        // Arrange
        _server.AddFile(_tempAudioFile);

        // Act
        var paths = _server.GetUploadedFilePaths();

        // Assert
        Assert.Empty(paths);
    }

    public void Dispose()
    {
        _server.Dispose();

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
