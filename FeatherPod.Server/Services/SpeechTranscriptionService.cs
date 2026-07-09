using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace FeatherPod.Server.Services;

/// <summary>
/// Wraps the Azure Speech transcription REST APIs for diarized transcription.
/// Primary path is the synchronous Fast endpoint (<c>/speechtotext/transcriptions:transcribe</c>);
/// the batch endpoint (<c>/speechtotext/v3.2/transcriptions</c>) is retained as a fallback
/// for audio that exceeds the diarized Fast cap (~2 hours). Produces VTT with per-speaker tags.
/// </summary>
public class SpeechTranscriptionService : ISpeechTranscriptionService
{
    public const string FastHttpClientName = "AzureSpeechFast";

    private const string BatchApiPath = "/speechtotext/v3.2/transcriptions";
    private const string FastApiPath = "/speechtotext/transcriptions:transcribe";
    private const string FastApiVersion = "2025-10-15";

    private static readonly JsonSerializerOptions CamelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TokenRequestContext SpeechTokenScope = new(["https://cognitiveservices.azure.com/.default"]);
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);

    private readonly string? _endpoint;
    private readonly string _locale;
    private readonly int _diarizationMaxSpeakers;
    private readonly DefaultAzureCredential _credential = new();
    private readonly HttpClient _httpClient = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SpeechTranscriptionService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _pollTimeout;
    private readonly object _tokenLock = new();
    private AccessToken _cachedToken;

    /// <summary>
    /// Whether transcription is available (AzureSpeech:Endpoint is configured).
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_endpoint);

    public SpeechTranscriptionService(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<SpeechTranscriptionService> logger)
    {
        _endpoint = configuration["AzureSpeech:Endpoint"]?.TrimEnd('/');
        _locale = configuration.GetValue("AzureSpeech:Locale", "en-US")!;
        _diarizationMaxSpeakers = configuration.GetValue("AzureSpeech:DiarizationMaxSpeakers", 6);
        _pollTimeout = TimeSpan.FromMinutes(configuration.GetValue("AzureSpeech:BatchTimeoutMinutes", 30));
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        if (IsAvailable)
        {
            _logger.LogInformation("Speech transcription enabled (endpoint: {Endpoint})", _endpoint);
        }
        else
        {
            _logger.LogInformation("AzureSpeech:Endpoint not configured; transcription disabled");
        }
    }

    /// <summary>
    /// Submit audio to the Fast Transcription endpoint and return diarized VTT.
    /// </summary>
    public async Task<string?> TranscribeFastAsync(Stream audio, string contentType, string? fileName, CancellationToken ct)
    {
        var definition = JsonSerializer.Serialize(new
        {
            locales = new[] { _locale },
            diarization = new { enabled = true, maxSpeakers = _diarizationMaxSpeakers },
        }, CamelCaseOptions);

        using var content = new MultipartFormDataContent();
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(audioContent, "audio", fileName ?? "audio");

        var definitionContent = new StringContent(definition, Encoding.UTF8, "application/json");
        content.Add(definitionContent, "definition");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}{FastApiPath}?api-version={FastApiVersion}")
        {
            Content = content,
        };
        await SetAuthHeaderAsync(request, ct);

        var fastClient = _httpClientFactory.CreateClient(FastHttpClientName);
        var stopwatch = Stopwatch.StartNew();
        using var response = await fastClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            ThrowForFastFailure(response.StatusCode, body);
        }

        using var doc = JsonDocument.Parse(body);
        var segments = FastTranscriptionParser.Parse(doc.RootElement);

        if (segments.Count == 0)
        {
            _logger.LogWarning("Fast transcription produced no recognized phrases");

            return null;
        }

        var audioMs = doc.RootElement.TryGetProperty("durationMilliseconds", out var dms) ? dms.GetInt64() : 0L;
        var wallMs = stopwatch.ElapsedMilliseconds;
        var ratio = audioMs > 0 && wallMs > 0 ? Math.Round((double)audioMs / wallMs, 2) : 0.0;
        _logger.LogInformation(
            "Fast transcription complete: {SegmentCount} segments, {SpeakerCount} speakers, AudioMs={AudioMs} WallMs={WallMs} Ratio={Ratio}x realtime",
            segments.Count,
            segments.Select(s => s.SpeakerId).Distinct().Count(),
            audioMs,
            wallMs,
            ratio);

        return VttSerializer.Serialize(segments);
    }

    private static void ThrowForFastFailure(HttpStatusCode statusCode, string body)
    {
        // 400 covers payload-too-large/duration-too-long ("AudioTooLong", "InvalidAudioLength").
        // 413 is the explicit Payload-Too-Large case. 404 means the endpoint isn't routed in
        // the region. All three are recoverable by falling back to the batch endpoint.
        if (statusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.NotFound)
        {
            throw new FastTranscriptionUnavailableException($"Fast transcription unavailable ({(int)statusCode}): {body}");
        }

        throw new HttpRequestException($"Speech API POST {FastApiPath} failed ({(int)statusCode}): {body}");
    }

    /// <summary>
    /// Submit a batch transcription job. Returns the self link (transcription URL).
    /// </summary>
    public async Task<string> SubmitAsync(string audioUrl, CancellationToken ct)
    {
        var requestBody = new
        {
            contentUrls = new[] { audioUrl },
            locale = _locale,
            displayName = $"FeatherPod-{Guid.NewGuid():N}",
            properties = new
            {
                diarizationEnabled = true,
                wordLevelTimestampsEnabled = false,
                punctuationMode = "DictatedAndAutomatic",
                profanityFilterMode = "None",
            },
        };

        var json = JsonSerializer.Serialize(requestBody, CamelCaseOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}{BatchApiPath}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        using var doc = await SendAndReadJsonAsync(request, ct);
        var selfLink = doc.RootElement.GetProperty("self").GetString()
            ?? throw new InvalidOperationException("Batch transcription response missing 'self' link");

        _logger.LogInformation("Submitted batch transcription: {SelfLink}", selfLink);

        return selfLink;
    }

    /// <summary>
    /// Poll until terminal state. Returns (status, filesListUrl, errorMessage).
    /// </summary>
    public async Task<(string Status, string? FilesListUrl, string? ErrorMessage)> PollUntilCompleteAsync(string transcriptionUrl, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _pollTimeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, transcriptionUrl);
            using var doc = await SendAndReadJsonAsync(request, ct);
            var root = doc.RootElement;

            var status = root.GetProperty("status").GetString() ?? "Unknown";
            _logger.LogDebug("Batch transcription poll: {Status}", status);

            if (status is BatchTranscriptionApi.SucceededStatus or BatchTranscriptionApi.FailedStatus)
            {
                string? filesListUrl = null;
                string? errorMessage = null;

                if (status is BatchTranscriptionApi.SucceededStatus
                    && root.TryGetProperty("links", out var links)
                    && links.TryGetProperty("files", out var files))
                {
                    filesListUrl = files.GetString();
                }

                if (status is BatchTranscriptionApi.FailedStatus
                    && root.TryGetProperty("properties", out var props)
                    && props.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                    var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    errorMessage = $"{code}: {message}";
                    _logger.LogWarning("Batch transcription failed: {Error}", errorMessage);
                }

                return (status, filesListUrl, errorMessage);
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Batch transcription timed out after {_pollTimeout} (last status: {status})");
            }

            await Task.Delay(_pollInterval, ct);
        }
    }

    /// <summary>
    /// Download the batch result and convert to VTT with speaker diarization.
    /// Returns null if no recognized phrases are found.
    /// </summary>
    public async Task<string?> GetResultAsVttAsync(string filesListUrl, CancellationToken ct)
    {
        // Step 1: List files and find the Transcription result
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, filesListUrl);
        using var listDoc = await SendAndReadJsonAsync(listRequest, ct);

        string? contentUrl = null;
        foreach (var value in listDoc.RootElement.GetProperty("values").EnumerateArray())
        {
            if (value.GetProperty("kind").GetString() is BatchTranscriptionApi.TranscriptionFileKind)
            {
                contentUrl = value.GetProperty("links").GetProperty("contentUrl").GetString();

                break;
            }
        }

        if (contentUrl is null)
        {
            _logger.LogWarning("No Transcription file found in batch result");

            return null;
        }

        // Step 2: Download the transcription content (SAS URL, no auth needed)
        using var contentResponse = await _httpClient.GetAsync(contentUrl, ct);
        contentResponse.EnsureSuccessStatusCode();

        var contentBody = await contentResponse.Content.ReadAsStringAsync(ct);
        using var contentDoc = JsonDocument.Parse(contentBody);

        var segments = BatchTranscriptionParser.Parse(contentDoc.RootElement);

        if (segments.Count == 0)
        {
            _logger.LogWarning("Batch transcription result contained no usable phrases");

            return null;
        }

        _logger.LogInformation("Batch transcription complete: {SegmentCount} segments from {SpeakerCount} speakers",
            segments.Count, segments.Select(s => s.SpeakerId).Distinct().Count());

        return VttSerializer.Serialize(segments);
    }

    /// <summary>
    /// Delete the batch transcription job from Azure (quota cleanup).
    /// </summary>
    public async Task DeleteAsync(string transcriptionUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, transcriptionUrl);
        await SetAuthHeaderAsync(request, ct);

        using var response = await _httpClient.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Deleted batch transcription: {Url}", transcriptionUrl);
        }
        else
        {
            _logger.LogWarning("Failed to delete batch transcription {Url}: {Status}", transcriptionUrl, response.StatusCode);
        }
    }

    /// <summary>
    /// Sets the bearer token, sends the request, and parses the response body as a <see cref="JsonDocument"/>.
    /// Throws <see cref="HttpRequestException"/> with the response status and body on a non-success response.
    /// The caller owns the returned <see cref="JsonDocument"/> and must dispose it.
    /// </summary>
    private async Task<JsonDocument> SendAndReadJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await SetAuthHeaderAsync(request, ct);

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Speech API {request.Method} {request.RequestUri?.AbsolutePath} failed ({(int)response.StatusCode}): {body}");
        }

        return JsonDocument.Parse(body);
    }

    private async Task SetAuthHeaderAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        // Read current cached token under a short in-memory lock (AccessToken is a struct,
        // so multi-field reads aren't torn-read safe without synchronization).
        AccessToken snapshot;
        lock (_tokenLock)
        {
            snapshot = _cachedToken;
        }

        // Algebraically equivalent to `ExpiresOn - TokenRefreshBuffer > UtcNow`, but avoids
        // underflow on the cold-start path where `_cachedToken` is default(AccessToken)
        // and `ExpiresOn == DateTimeOffset.MinValue`.
        if (snapshot.ExpiresOn > DateTimeOffset.UtcNow + TokenRefreshBuffer)
        {
            return snapshot.Token;
        }

        // Refresh outside the lock. A concurrent caller may also refresh, but that's fine:
        // DefaultAzureCredential caches internally, so the wasted work is minimal.
        var fresh = await _credential.GetTokenAsync(SpeechTokenScope, ct);

        lock (_tokenLock)
        {
            _cachedToken = fresh;
        }

        return fresh.Token;
    }
}
