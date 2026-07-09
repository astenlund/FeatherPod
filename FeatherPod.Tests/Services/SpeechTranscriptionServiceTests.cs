using System.Net;
using System.Text;
using Azure.Core;
using FeatherPod.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Pure unit tests for the batch REST paths of <see cref="SpeechTranscriptionService"/>, driven by a
/// stub <see cref="HttpMessageHandler"/> injected through a stub <see cref="IHttpClientFactory"/> and a
/// stub <see cref="TokenCredential"/>. Covers success-path JSON parsing, the pinned non-2xx error
/// format, that parsed values outlive their <see cref="System.Text.Json.JsonDocument"/> scope, and the
/// single 401-triggered token refresh + retry.
/// </summary>
[Collection("Sequential")]
public class SpeechTranscriptionServiceTests
{
    private const string Endpoint = "https://speech.example";

    [Fact]
    public async Task SubmitAsync_SuccessResponse_ReturnsSelfLink()
    {
        // Arrange
        var handler = new QueuedHandler(Json(HttpStatusCode.Created, """{"self":"https://azure.example/transcriptions/abc"}"""));
        var service = CreateService(handler, new StubTokenCredential());

        // Act
        var selfLink = await service.SubmitAsync("https://blob.example/audio.mp3", CancellationToken.None);

        // Assert -- the returned string is read via GetString and survives the JsonDocument's using scope.
        Assert.Equal("https://azure.example/transcriptions/abc", selfLink);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SubmitAsync_NonSuccessResponse_ThrowsWithPinnedFormatAndBody()
    {
        // Arrange
        var handler = new QueuedHandler(Json(HttpStatusCode.BadRequest, "bad payload"));
        var service = CreateService(handler, new StubTokenCredential());

        // Act
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.SubmitAsync("https://blob.example/audio.mp3", CancellationToken.None));

        // Assert
        Assert.Equal("Speech API POST /speechtotext/v3.2/transcriptions failed (400): bad payload", ex.Message);
    }

    [Fact]
    public async Task PollUntilCompleteAsync_Succeeded_ReturnsStatusAndFilesUrl()
    {
        // Arrange
        var handler = new QueuedHandler(Json(HttpStatusCode.OK, """{"status":"Succeeded","links":{"files":"https://azure.example/files"}}"""));
        var service = CreateService(handler, new StubTokenCredential());

        // Act
        var (status, filesListUrl, errorMessage) = await service.PollUntilCompleteAsync("https://azure.example/transcriptions/abc", CancellationToken.None);

        // Assert
        Assert.Equal("Succeeded", status);
        Assert.Equal("https://azure.example/files", filesListUrl);
        Assert.Null(errorMessage);
    }

    [Fact]
    public async Task PollUntilCompleteAsync_Failed_ReturnsErrorMessage()
    {
        // Arrange
        var handler = new QueuedHandler(Json(HttpStatusCode.OK, """{"status":"Failed","properties":{"error":{"code":"InvalidData","message":"audio too short"}}}"""));
        var service = CreateService(handler, new StubTokenCredential());

        // Act
        var (status, filesListUrl, errorMessage) = await service.PollUntilCompleteAsync("https://azure.example/transcriptions/abc", CancellationToken.None);

        // Assert
        Assert.Equal("Failed", status);
        Assert.Null(filesListUrl);
        Assert.Equal("InvalidData: audio too short", errorMessage);
    }

    [Fact]
    public async Task GetResultAsVttAsync_SuccessResponses_ReturnsVtt()
    {
        // Arrange -- first the files list (auth), then the SAS content download.
        var listBody = """{"values":[{"kind":"Transcription","links":{"contentUrl":"https://sas.example/content"}}]}""";
        var contentBody = """{"recognizedPhrases":[{"offsetInTicks":0.0,"durationInTicks":20000000.0,"speaker":0,"nBest":[{"display":"Hello world."}]}]}""";
        var handler = new QueuedHandler(Json(HttpStatusCode.OK, listBody), Json(HttpStatusCode.OK, contentBody));
        var service = CreateService(handler, new StubTokenCredential());

        // Act
        var vtt = await service.GetResultAsVttAsync("https://azure.example/files", CancellationToken.None);

        // Assert -- the VTT is built while the JsonDocument is alive and remains valid after it is disposed.
        Assert.NotNull(vtt);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("Hello world.", vtt);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task SubmitAsync_Unauthorized_RefreshesTokenAndRetriesOnce()
    {
        // Arrange -- first call is rejected with 401, the retry succeeds.
        var credential = new StubTokenCredential();
        var handler = new QueuedHandler(
            Json(HttpStatusCode.Unauthorized, "expired"),
            Json(HttpStatusCode.Created, """{"self":"https://azure.example/transcriptions/retry"}"""));
        var service = CreateService(handler, credential);

        // Act
        var selfLink = await service.SubmitAsync("https://blob.example/audio.mp3", CancellationToken.None);

        // Assert -- exactly one retry, and the cached token was dropped so a fresh token was fetched.
        Assert.Equal("https://azure.example/transcriptions/retry", selfLink);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, credential.TokenRequests);
    }

    [Fact]
    public async Task SubmitAsync_UnauthorizedTwice_ThrowsAfterSingleRetry()
    {
        // Arrange -- both attempts return 401; the service must give up after one retry.
        var credential = new StubTokenCredential();
        var handler = new QueuedHandler(
            Json(HttpStatusCode.Unauthorized, "expired"),
            Json(HttpStatusCode.Unauthorized, "still expired"));
        var service = CreateService(handler, credential);

        // Act
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.SubmitAsync("https://blob.example/audio.mp3", CancellationToken.None));

        // Assert
        Assert.Equal("Speech API POST /speechtotext/v3.2/transcriptions failed (401): still expired", ex.Message);
        Assert.Equal(2, handler.CallCount);
    }

    private static SpeechTranscriptionService CreateService(HttpMessageHandler handler, TokenCredential credential)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureSpeech:Endpoint"] = Endpoint,
            })
            .Build();

        return new SpeechTranscriptionService(configuration, new SingleHandlerHttpClientFactory(handler), NullLogger<SpeechTranscriptionService>.Instance, credential);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueuedHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Unexpected HTTP call; no queued response remains.");
            }

            var response = _responses.Dequeue();
            response.RequestMessage = request;

            return Task.FromResult(response);
        }
    }

    private sealed class SingleHandlerHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public int TokenRequests { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            TokenRequests++;

            return new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
