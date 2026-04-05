using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace FeatherPod.Server.Services;

/// <summary>
/// Wraps the Azure Speech batch transcription REST API (v3.2) for diarized transcription.
/// Produces VTT output with per-speaker voice tags.
/// </summary>
public class SpeechTranscriptionService
{
    private const string BatchApiPath = "/speechtotext/v3.2/transcriptions";

    private static readonly JsonSerializerOptions CamelCaseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TokenRequestContext SpeechTokenScope = new(["https://cognitiveservices.azure.com/.default"]);

    private readonly string? _endpoint;
    private readonly string _locale;
    private readonly DefaultAzureCredential _credential = new();
    private readonly HttpClient _httpClient = new();
    private readonly ILogger<SpeechTranscriptionService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _pollTimeout;

    /// <summary>
    /// Whether transcription is available (AzureSpeech:Endpoint is configured).
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_endpoint);

    public SpeechTranscriptionService(IConfiguration configuration, ILogger<SpeechTranscriptionService> logger)
    {
        _endpoint = configuration["AzureSpeech:Endpoint"];
        _locale = configuration.GetValue("AzureSpeech:Locale", "en-US")!;
        _pollTimeout = TimeSpan.FromMinutes(configuration.GetValue("AzureSpeech:BatchTimeoutMinutes", 30));
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
        await SetAuthHeaderAsync(request, ct);

        using var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Batch transcription submit failed ({response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
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
            await SetAuthHeaderAsync(request, ct);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var status = root.GetProperty("status").GetString() ?? "Unknown";
            _logger.LogDebug("Batch transcription poll: {Status}", status);

            if (status is "Succeeded" or "Failed")
            {
                string? filesListUrl = null;
                string? errorMessage = null;

                if (status is "Succeeded"
                    && root.TryGetProperty("links", out var links)
                    && links.TryGetProperty("files", out var files))
                {
                    filesListUrl = files.GetString();
                }

                if (status is "Failed"
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
        await SetAuthHeaderAsync(listRequest, ct);

        using var listResponse = await _httpClient.SendAsync(listRequest, ct);
        listResponse.EnsureSuccessStatusCode();

        var listBody = await listResponse.Content.ReadAsStringAsync(ct);
        using var listDoc = JsonDocument.Parse(listBody);

        string? contentUrl = null;
        foreach (var value in listDoc.RootElement.GetProperty("values").EnumerateArray())
        {
            if (value.GetProperty("kind").GetString() is "Transcription")
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

        if (!contentDoc.RootElement.TryGetProperty("recognizedPhrases", out var phrases) || phrases.GetArrayLength() == 0)
        {
            _logger.LogWarning("Batch transcription result has no recognizedPhrases");

            return null;
        }

        // Convert to DiarizedSegments.
        // Note: offsetInTicks/durationInTicks are returned as floats (e.g. 400000.0), so we use
        // GetDouble() + cast rather than GetInt64() which throws FormatException on floats.
        var segments = new List<DiarizedSegment>();
        foreach (var phrase in phrases.EnumerateArray())
        {
            var speaker = phrase.TryGetProperty("speaker", out var sp) ? sp.ToString() : "0";
            var offsetTicks = (long)phrase.GetProperty("offsetInTicks").GetDouble();
            var durationTicks = (long)phrase.GetProperty("durationInTicks").GetDouble();
            var display = phrase.GetProperty("nBest")[0].GetProperty("display").GetString() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(display))
            {
                segments.Add(new DiarizedSegment(offsetTicks, durationTicks, $"Speaker {speaker}", display));
            }
        }

        if (segments.Count == 0)
        {
            _logger.LogWarning("Batch transcription produced segments but all were empty");

            return null;
        }

        _logger.LogInformation("Batch transcription complete: {SegmentCount} segments from {SpeakerCount} speakers",
            segments.Count, segments.Select(s => s.SpeakerId).Distinct().Count());

        return SerializeDiarizedVtt(segments);
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
    /// Serialize diarized segments to VTT with speaker voice tags.
    /// Public and static for testability.
    /// </summary>
    public static string SerializeDiarizedVtt(IReadOnlyList<DiarizedSegment> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        foreach (var segment in segments)
        {
            var start = TimeSpan.FromTicks(segment.OffsetTicks);
            var end = start + TimeSpan.FromTicks(segment.DurationTicks);

            sb.AppendLine($"{FormatVttTimestamp(start)} --> {FormatVttTimestamp(end)}");
            sb.AppendLine($"<v {segment.SpeakerId}>{segment.Text}</v>");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static string FormatVttTimestamp(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private async Task SetAuthHeaderAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(SpeechTokenScope, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    /// <summary>
    /// A single diarized speech segment.
    /// </summary>
    public record DiarizedSegment(long OffsetTicks, long DurationTicks, string SpeakerId, string Text);
}
