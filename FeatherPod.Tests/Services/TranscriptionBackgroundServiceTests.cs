using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Drives <see cref="TranscriptionBackgroundService"/> via a fake <see cref="ISpeechTranscriptionService"/>
/// and the real <see cref="ITranscriptionChannel"/>. Covers Fast and batch outcomes plus the
/// Fast-to-batch fallback, and verifies the quota-cleanup DeleteAsync call in the finally block.
/// The shared <see cref="_service"/> instance has <c>UseFastTranscription=false</c> so legacy
/// batch tests run unchanged; Fast-path tests build a sibling instance via <see cref="BuildService"/>.
/// </summary>
[Collection("Sequential")]
public class TranscriptionBackgroundServiceTests : IAsyncLifetime
{
    private const string JobId = "job-1";

    private readonly FakeSpeechTranscriptionService _speech = new();
    private readonly RecordingBlobService _blob = new();
    private readonly InMemoryJobService _jobs = new();
    private readonly TranscriptionChannel _channel = new();
    private readonly StubLifetime _lifetime = new();
    private readonly FakeAudioDurationProbe _duration = new();
    private readonly TranscriptionBackgroundService _service;

    private TranscriptionBackgroundService? _fastService;

    public TranscriptionBackgroundServiceTests()
    {
        _service = BuildService(useFast: false);
    }

    public Task InitializeAsync() => _service.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        _channel.Complete();
        await _service.StopAsync(CancellationToken.None);

        if (_fastService != null)
        {
            await _fastService.StopAsync(CancellationToken.None);
        }

        _lifetime.Dispose();
    }

    [Fact]
    public async Task SucceededStatus_UploadsVttAndMarksCompleted()
    {
        // Arrange
        _jobs.SetEntity(CreateEntity());
        _speech.SubmitReturns = "https://azure.example/transcriptions/abc";
        _speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/abc/files", null);
        _speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:02.000\r\n<v Speaker 0>Hi.</v>\r\n";

        // Act
        await SubmitAndWaitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Null(entity.TranscriptionError);
        Assert.Single(_blob.UploadedTranscripts);
        Assert.Equal("test-feed", _blob.UploadedTranscripts[0].FeedId);
        Assert.Equal("ep-1", _blob.UploadedTranscripts[0].EpisodeId);
        Assert.StartsWith("WEBVTT", _blob.UploadedTranscripts[0].Vtt);
        Assert.Contains("https://azure.example/transcriptions/abc", _speech.DeletedUrls);
    }

    [Fact]
    public async Task FailedStatus_MarksFailedWithErrorMessage_AndDeletes()
    {
        // Arrange
        _jobs.SetEntity(CreateEntity());
        _speech.SubmitReturns = "https://azure.example/transcriptions/def";
        _speech.PollReturns = ("Failed", null, "BadRequest: audio too short");

        // Act
        await SubmitAndWaitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("BadRequest: audio too short", entity.TranscriptionError);
        Assert.Empty(_blob.UploadedTranscripts);
        Assert.Contains("https://azure.example/transcriptions/def", _speech.DeletedUrls);
    }

    [Fact]
    public async Task GetResultReturnsNull_MarksFailedProducedNoOutput_AndDeletes()
    {
        // Arrange
        _jobs.SetEntity(CreateEntity());
        _speech.SubmitReturns = "https://azure.example/transcriptions/ghi";
        _speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/ghi/files", null);
        _speech.GetResultReturns = null;

        // Act
        await SubmitAndWaitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("Transcription produced no output", entity.TranscriptionError);
        Assert.Empty(_blob.UploadedTranscripts);
        Assert.Contains("https://azure.example/transcriptions/ghi", _speech.DeletedUrls);
    }

    [Fact]
    public async Task SubmitThrows_MarksFailed_DoesNotCallDelete()
    {
        // Arrange
        _jobs.SetEntity(CreateEntity());
        _speech.SubmitThrows = new HttpRequestException("Speech API POST /speechtotext/v3.2/transcriptions failed (401): unauthorized");

        // Act
        await SubmitAndWaitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.StartsWith("Speech API POST /speechtotext/v3.2/transcriptions failed", entity.TranscriptionError);
        Assert.Empty(_speech.DeletedUrls);
    }

    [Fact]
    public async Task PollThrows_MarksFailed_AndStillCallsDelete()
    {
        // Arrange
        _jobs.SetEntity(CreateEntity());
        _speech.SubmitReturns = "https://azure.example/transcriptions/jkl";
        _speech.PollThrows = new TimeoutException("Batch transcription timed out");

        // Act
        await SubmitAndWaitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("Batch transcription timed out", entity.TranscriptionError);
        Assert.Contains("https://azure.example/transcriptions/jkl", _speech.DeletedUrls);
    }

    [Fact]
    public async Task FastSucceeded_UploadsVttAndMarksCompleted_NoBatchCalls()
    {
        // Arrange
        _jobs.SetEntity(CreateEntity());
        _duration.Duration = TimeSpan.FromMinutes(20);
        _speech.FastReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Fast.</v>\r\n";

        // Act
        await StartFastServiceAndSubmitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Null(entity.TranscriptionError);
        Assert.StartsWith("WEBVTT", _blob.UploadedTranscripts[0].Vtt);
        Assert.Equal(1, _speech.FastCallCount);
        Assert.Equal(0, _speech.SubmitCallCount);
        Assert.Empty(_speech.DeletedUrls);
    }

    [Fact]
    public async Task FastReturnsNull_MarksFailedNoOutput_NoBatchFallback()
    {
        // Arrange — null is "ran fine but said nothing", not "endpoint unavailable" — so no batch retry.
        _jobs.SetEntity(CreateEntity());
        _duration.Duration = TimeSpan.FromMinutes(5);
        _speech.FastReturns = null;

        // Act
        await StartFastServiceAndSubmitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("Transcription produced no output", entity.TranscriptionError);
        Assert.Empty(_blob.UploadedTranscripts);
        Assert.Equal(1, _speech.FastCallCount);
        Assert.Equal(0, _speech.SubmitCallCount);
    }

    [Fact]
    public async Task FastThrowsGenericException_MarksFailed_NoBatchFallback()
    {
        // Arrange
        _jobs.SetEntity(CreateEntity());
        _duration.Duration = TimeSpan.FromMinutes(5);
        _speech.FastThrows = new HttpRequestException("Speech API POST /speechtotext/transcriptions:transcribe failed (500): boom");

        // Act
        await StartFastServiceAndSubmitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.StartsWith("Speech API POST /speechtotext/transcriptions:transcribe", entity.TranscriptionError);
        Assert.Equal(1, _speech.FastCallCount);
        Assert.Equal(0, _speech.SubmitCallCount);
        Assert.Empty(_speech.DeletedUrls);
    }

    [Fact]
    public async Task FastUnavailable_FallsBackToBatchSucceeded()
    {
        // Arrange
        _jobs.SetEntity(CreateEntity());
        _duration.Duration = TimeSpan.FromMinutes(5);
        _speech.FastThrows = new FastTranscriptionUnavailableException("AudioTooLong");
        _speech.SubmitReturns = "https://azure.example/transcriptions/fallback";
        _speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/fallback/files", null);
        _speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:02.000\r\n<v Speaker 0>Batch.</v>\r\n";

        // Act
        await StartFastServiceAndSubmitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Null(entity.TranscriptionError);
        Assert.StartsWith("WEBVTT", _blob.UploadedTranscripts[0].Vtt);
        Assert.Contains("Batch", _blob.UploadedTranscripts[0].Vtt);
        Assert.Equal(1, _speech.FastCallCount);
        Assert.Contains("https://azure.example/transcriptions/fallback", _speech.DeletedUrls);
    }

    [Fact]
    public async Task DurationOverFastCap_SkipsFastAndUsesBatch()
    {
        // Arrange — 200 minutes is > the 110-minute Fast cap.
        _jobs.SetEntity(CreateEntity());
        _duration.Duration = TimeSpan.FromMinutes(200);
        _speech.SubmitReturns = "https://azure.example/transcriptions/long";
        _speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/long/files", null);
        _speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Long.</v>\r\n";

        // Act
        await StartFastServiceAndSubmitAsync();

        // Assert
        var entity = _jobs.GetEntity(JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Equal(0, _speech.FastCallCount);
        Assert.Contains("https://azure.example/transcriptions/long", _speech.DeletedUrls);
    }

    private static JobStatusEntity CreateEntity() => new()
    {
        PartitionKey = JobStorageNames.JobsPartitionKey,
        RowKey = JobId,
        Status = nameof(JobStatus.Processing),
        FeedId = "test-feed",
        FileName = "episode.mp3",
        Title = "Test Episode",
        Source = nameof(UploadSource.Browser),
        OriginalFileSize = 1024,
        TranscriptionStatus = TranscriptionStatuses.Queued,
        NormalizationComplete = false,
        QueuedAt = DateTimeOffset.UtcNow,
    };

    private async Task SubmitAndWaitAsync()
    {
        await _channel.SubmitAsync(new TranscriptionRequest
        {
            JobId = JobId,
            FeedId = "test-feed",
            FileName = "episode.mp3",
            EpisodeId = "ep-1",
        });

        await _jobs.WaitForProcessingCompleteAsync(JobId, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Spin up a Fast-enabled sibling service that drains the same channel as the legacy one,
    /// then submit a request and wait for terminal. The base ctor's batch service has already
    /// drained nothing because no request was queued before this is called.
    /// </summary>
    private async Task StartFastServiceAndSubmitAsync()
    {
        _fastService = BuildService(useFast: true);
        await _fastService.StartAsync(CancellationToken.None);
        // Stop the batch service so only the Fast service consumes from the shared channel.
        await _service.StopAsync(CancellationToken.None);

        await SubmitAndWaitAsync();
    }

    private TranscriptionBackgroundService BuildService(bool useFast)
    {
        var episodeService = new EpisodeService(_blob, NullLogger<EpisodeService>.Instance);
        var completionService = new JobCompletionService(
            _jobs,
            episodeService,
            _blob,
            new NullJobProgressChannel(),
            new NullFeedEventChannel(),
            new StubPushNotificationService(_blob, episodeService, _jobs),
            NullLogger<JobCompletionService>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureSpeech:MaxConcurrent"] = "1",
                ["AzureSpeech:UseFastTranscription"] = useFast ? "true" : "false",
                ["AzureSpeech:FastMaxDurationMinutes"] = "110",
            })
            .Build();

        return new TranscriptionBackgroundService(
            _channel,
            _speech,
            _blob,
            _jobs,
            new NullJobProgressChannel(),
            completionService,
            _duration,
            _lifetime,
            configuration,
            NullLogger<TranscriptionBackgroundService>.Instance);
    }

    private sealed class FakeSpeechTranscriptionService : ISpeechTranscriptionService
    {
        public bool IsAvailable { get; set; } = true;

        public string SubmitReturns { get; set; } = string.Empty;

        public Exception? SubmitThrows { get; set; }

        public (string Status, string? FilesListUrl, string? ErrorMessage) PollReturns { get; set; }

        public Exception? PollThrows { get; set; }

        public string? GetResultReturns { get; set; }

        public string? FastReturns { get; set; }

        public Exception? FastThrows { get; set; }

        public int FastCallCount { get; private set; }

        public int SubmitCallCount { get; private set; }

        public List<string> DeletedUrls { get; } = [];

        public Task<string?> TranscribeFastAsync(Stream audio, string contentType, string? fileName, CancellationToken ct)
        {
            FastCallCount++;

            if (FastThrows != null)
            {
                throw FastThrows;
            }

            return Task.FromResult(FastReturns);
        }

        public Task<string> SubmitAsync(string audioUrl, CancellationToken ct)
        {
            SubmitCallCount++;

            if (SubmitThrows != null)
            {
                throw SubmitThrows;
            }

            return Task.FromResult(SubmitReturns);
        }

        public Task<(string Status, string? FilesListUrl, string? ErrorMessage)> PollUntilCompleteAsync(string transcriptionUrl, CancellationToken ct)
        {
            if (PollThrows != null)
            {
                throw PollThrows;
            }

            return Task.FromResult(PollReturns);
        }

        public Task<string?> GetResultAsVttAsync(string filesListUrl, CancellationToken ct)
        {
            return Task.FromResult(GetResultReturns);
        }

        public Task DeleteAsync(string transcriptionUrl, CancellationToken ct)
        {
            DeletedUrls.Add(transcriptionUrl);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudioDurationProbe : IAudioDurationProbe
    {
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;

        public Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken ct) => Task.FromResult(Duration);
    }
}
