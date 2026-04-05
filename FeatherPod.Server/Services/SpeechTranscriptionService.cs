using System.Text;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;

namespace FeatherPod.Server.Services;

/// <summary>
/// Wraps the Azure Speech SDK ConversationTranscriber for diarized transcription.
/// Produces VTT output with per-speaker voice tags.
/// </summary>
public class SpeechTranscriptionService
{
    private readonly string? _endpoint;
    private readonly ILogger<SpeechTranscriptionService> _logger;

    /// <summary>
    /// Whether transcription is available (AzureSpeech:Endpoint is configured).
    /// </summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_endpoint);

    public SpeechTranscriptionService(IConfiguration configuration, ILogger<SpeechTranscriptionService> logger)
    {
        _endpoint = configuration["AzureSpeech:Endpoint"];
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
    /// Transcribe a 16kHz mono WAV PCM audio stream with speaker diarization.
    /// Returns VTT string with speaker voice tags, or null on failure.
    /// </summary>
    public async Task<string?> TranscribeAsync(
        Stream audioStream,
        TimeSpan totalDuration,
        Action<double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Speech transcription is not configured");
        }

        var speechConfig = SpeechConfig.FromEndpoint(new Uri(_endpoint!), new DefaultAzureCredential());
        speechConfig.SpeechRecognitionLanguage = "en-US";

        // 16kHz mono 16-bit PCM
        var audioFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        using var pushStream = AudioInputStream.CreatePushStream(audioFormat);
        using var audioConfig = AudioConfig.FromStreamInput(pushStream);
        using var transcriber = new ConversationTranscriber(speechConfig, audioConfig);

        var events = new List<DiarizedSegment>();
        var eventsLock = new object();
        var sessionStoppedTcs = new TaskCompletionSource<bool>();
        // Written once from Canceled handler, read after await (which provides memory barrier)
        string? cancelReason = null;

        transcriber.Transcribed += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                lock (eventsLock)
                {
                    events.Add(new DiarizedSegment(
                        e.Result.OffsetInTicks,
                        e.Result.Duration.Ticks,
                        e.Result.SpeakerId ?? "Unknown",
                        e.Result.Text));
                }

                if (totalDuration > TimeSpan.Zero)
                {
                    var progress = Math.Min(100.0, (double)e.Result.OffsetInTicks / totalDuration.Ticks * 100);
                    progressCallback?.Invoke(progress);
                }
            }
        };

        transcriber.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
            {
                cancelReason = $"Speech SDK error: {e.ErrorCode} - {e.ErrorDetails}";
                _logger.LogError("ConversationTranscriber cancelled: {ErrorCode} - {Details}", e.ErrorCode, e.ErrorDetails);
            }

            sessionStoppedTcs.TrySetResult(false);
        };

        transcriber.SessionStopped += (_, _) =>
        {
            sessionStoppedTcs.TrySetResult(true);
        };

        await transcriber.StartTranscribingAsync();

        // Push audio bytes to the SDK
        var buffer = new byte[32 * 1024];
        int bytesRead;
        while ((bytesRead = await audioStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            pushStream.Write(buffer, bytesRead);
        }

        pushStream.Close();

        // Wait for processing to complete
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            await sessionStoppedTcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out waiting for ConversationTranscriber session to stop");
        }

        try
        {
            await transcriber.StopTranscribingAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping ConversationTranscriber");
        }

        if (cancelReason != null)
        {
            _logger.LogWarning("Transcription cancelled with error: {Reason}", cancelReason);

            return null;
        }

        // Snapshot events under lock (SDK events are done after StopTranscribingAsync)
        List<DiarizedSegment> snapshot;
        lock (eventsLock)
        {
            snapshot = [.. events];
        }

        if (snapshot.Count == 0)
        {
            _logger.LogWarning("Transcription produced no segments");

            return null;
        }

        progressCallback?.Invoke(100);
        _logger.LogInformation("Transcription complete: {SegmentCount} segments from {SpeakerCount} speakers",
            snapshot.Count, snapshot.Select(e => e.SpeakerId).Distinct().Count());

        return SerializeDiarizedVtt(snapshot);
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

    /// <summary>
    /// A single diarized speech segment.
    /// </summary>
    public record DiarizedSegment(long OffsetTicks, long DurationTicks, string SpeakerId, string Text);
}
