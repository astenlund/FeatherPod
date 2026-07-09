using System.Net;
using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class EpisodeNoteCommandTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? LastMethod { get; private set; }

        public string? LastPath { get; private set; }

        public string? LastBody { get; private set; }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public string ResponseBody { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;

            if (request.Content != null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(StatusCode) { Content = new StringContent(ResponseBody) };
        }
    }

    private static HttpClient CreateClient(CapturingHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    [Fact]
    public async Task UpdateEpisodeNoteAsync_SendsNoteInPatchBody()
    {
        // Arrange
        var handler = new CapturingHandler();
        using var client = CreateClient(handler);

        // Act
        var result = await EpisodeHelpers.UpdateEpisodeNoteAsync(client, "my-feed", "ep123", "Use title case");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(HttpMethod.Patch, handler.LastMethod);
        Assert.Equal("/api/feeds/my-feed/episodes/ep123", handler.LastPath);
        Assert.Contains("\"note\":\"Use title case\"", handler.LastBody);
    }

    [Fact]
    public async Task UpdateEpisodeNoteAsync_ClearSendsEmptyNote()
    {
        // Arrange
        var handler = new CapturingHandler();
        using var client = CreateClient(handler);

        // Act
        var result = await EpisodeHelpers.UpdateEpisodeNoteAsync(client, "my-feed", "ep123", string.Empty);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("\"note\":\"\"", handler.LastBody);
    }

    [Fact]
    public async Task UpdateEpisodeNoteAsync_NotFound_ReturnsFailure()
    {
        // Arrange
        var handler = new CapturingHandler { StatusCode = HttpStatusCode.NotFound };
        using var client = CreateClient(handler);

        // Act
        var result = await EpisodeHelpers.UpdateEpisodeNoteAsync(client, "my-feed", "missing", "note");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Episode not found", result.ErrorMessage);
    }

    [Fact]
    public async Task SuggestTitleAsync_WithNote_SendsNoteInBody()
    {
        // Arrange
        var handler = new CapturingHandler { ResponseBody = "{\"suggestedTitle\":\"Great Episode\"}" };
        using var client = CreateClient(handler);

        // Act
        var suggestion = await EpisodeHelpers.SuggestTitleAsync(client, "my-feed", "ep123", "some context");

        // Assert
        Assert.Equal("Great Episode", suggestion);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/api/feeds/my-feed/episodes/ep123/suggest-title", handler.LastPath);
        Assert.Contains("\"note\":\"some context\"", handler.LastBody);
    }

    [Fact]
    public async Task SuggestTitleAsync_WithoutNote_SendsNoBody()
    {
        // Arrange
        var handler = new CapturingHandler { ResponseBody = "{\"suggestedTitle\":\"Great Episode\"}" };
        using var client = CreateClient(handler);

        // Act
        var suggestion = await EpisodeHelpers.SuggestTitleAsync(client, "my-feed", "ep123");

        // Assert
        Assert.Equal("Great Episode", suggestion);
        Assert.Null(handler.LastBody);
    }
}
