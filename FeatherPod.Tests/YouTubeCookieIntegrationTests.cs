using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FeatherPod.Tests;

[Collection("IntegrationTests")]
public class YouTubeCookieIntegrationTests : IDisposable
{
    private readonly FeatherPodWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private const string AdminApiKey = FeatherPodWebApplicationFactory.ApiKey;

    public YouTubeCookieIntegrationTests()
    {
        _factory = new();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private HttpRequestMessage CreateCookieUploadRequest(string cookieContent, string apiKey)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(cookieContent));
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        content.Add(fileContent, "file", "cookies.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/youtube/cookies");
        request.Content = content;
        request.Headers.Add("X-API-Key", apiKey);

        return request;
    }

    [AzuriteFact]
    public async Task UploadCookies_WithValidNetscapeFile_ReturnsSuccess()
    {
        // Arrange
        var cookieContent = """
            # Netscape HTTP Cookie File
            .youtube.com	TRUE	/	TRUE	0	VISITOR_INFO1_LIVE	abc123
            .youtube.com	TRUE	/	TRUE	0	YSC	def456
            """;

        using var request = CreateCookieUploadRequest(cookieContent, AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal("Cookies uploaded successfully", doc.GetProperty("message").GetString());
    }

    [AzuriteFact]
    public async Task GetCookieStatus_AfterUpload_ShowsUploadInfo()
    {
        // Arrange - upload cookies first
        var cookieContent = "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tabc";
        using var uploadRequest = CreateCookieUploadRequest(cookieContent, AdminApiKey);
        var uploadResponse = await _client.SendAsync(uploadRequest);
        uploadResponse.EnsureSuccessStatusCode();

        // Act
        var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/youtube/cookies/status");
        statusRequest.Headers.Add("X-API-Key", AdminApiKey);
        var response = await _client.SendAsync(statusRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.True(doc.GetProperty("hasCookies").GetBoolean());
        Assert.NotNull(doc.GetProperty("uploadedAt").GetString());
        Assert.Equal("test-admin", doc.GetProperty("uploadedBy").GetString());
    }

    [AzuriteFact]
    public async Task GetCookieStatus_NoCookiesUploaded_ShowsNoCookies()
    {
        // Arrange - ensure no cookies exist by deleting any that might be there
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/youtube/cookies");
        deleteRequest.Headers.Add("X-API-Key", AdminApiKey);
        await _client.SendAsync(deleteRequest);

        // Act
        var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/youtube/cookies/status");
        statusRequest.Headers.Add("X-API-Key", AdminApiKey);
        var response = await _client.SendAsync(statusRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.False(doc.GetProperty("hasCookies").GetBoolean());
    }

    [AzuriteFact]
    public async Task UploadCookies_WithEmptyFile_ReturnsBadRequest()
    {
        // Arrange
        using var request = CreateCookieUploadRequest("", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [AzuriteFact]
    public async Task UploadCookies_WithInvalidFormat_ReturnsBadRequest()
    {
        // Arrange - content that doesn't look like a Netscape cookie file
        using var request = CreateCookieUploadRequest("this is not a cookie file at all", AdminApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Contains("Invalid cookie file format", doc.GetProperty("error").GetString());
    }

    [AzuriteFact]
    public async Task DeleteCookies_AfterUpload_RemovesCookies()
    {
        // Arrange - upload cookies first
        var cookieContent = "# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tabc";
        using var uploadRequest = CreateCookieUploadRequest(cookieContent, AdminApiKey);
        var uploadResponse = await _client.SendAsync(uploadRequest);
        uploadResponse.EnsureSuccessStatusCode();

        // Act
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/youtube/cookies");
        deleteRequest.Headers.Add("X-API-Key", AdminApiKey);
        var deleteResponse = await _client.SendAsync(deleteRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/youtube/cookies/status");
        statusRequest.Headers.Add("X-API-Key", AdminApiKey);
        var statusResponse = await _client.SendAsync(statusRequest);
        var json = await statusResponse.Content.ReadAsStringAsync();
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.False(doc.GetProperty("hasCookies").GetBoolean());
    }
}
