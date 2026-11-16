using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// Hosts a single <see cref="TranscriptionBackgroundService"/> for tests, wired to a fake
/// <see cref="ISpeechTranscriptionService"/> and the real <see cref="ITranscriptionChannel"/>.
/// The <c>useFast</c> flag selects the batch or Fast routing path; each test class owns one
/// harness so exactly one service drains the shared channel (no start-then-stop coordination).
/// </summary>
public sealed class TranscriptionTestHarness : IAsyncLifetime
{
    public const string JobId = "job-1";

    public const string DefaultFileName = "episode.mp3";

    private readonly TranscriptionChannel _channel = new();
    private readonly StubLifetime _lifetime = new();
    private readonly TranscriptionBackgroundService _service;

    public TranscriptionTestHarness(bool useFast)
    {
        _service = BuildService(useFast);
    }

    public FakeSpeechTranscriptionService Speech { get; } = new();

    public RecordingBlobService Blob { get; } = new();

    public InMemoryJobService Jobs { get; } = new();

    public FakeAudioDurationProbe Duration { get; } = new();

    public FakeWavConverter Converter { get; } = new();

    public Task InitializeAsync() => _service.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        _channel.Complete();
        await _service.StopAsync(CancellationToken.None);
        _lifetime.Dispose();
    }

    /// <summary>
    /// Path the service downloads the pending blob to for <paramref name="fileName"/>, so tests can check its cleanup.
    /// </summary>
    public static string TempInputPath(string fileName) => Path.Combine(Path.GetTempPath(), "FeatherPod", $"transcribe-{JobId}{Path.GetExtension(fileName)}");

    /// <summary>
    /// Path the service converts to, so tests can check its cleanup.
    /// </summary>
    public static string TempWavPath => Path.Combine(Path.GetTempPath(), "FeatherPod", $"transcribe-{JobId}.wav");

    public static JobStatusEntity CreateEntity(string fileName = DefaultFileName) => new()
    {
        PartitionKey = JobStorageNames.JobsPartitionKey,
        RowKey = JobId,
        Status = nameof(JobStatus.Processing),
        FeedId = "test-feed",
        FileName = fileName,
        Title = "Test Episode",
        Source = nameof(UploadSource.Browser),
        OriginalFileSize = 1024,
        TranscriptionStatus = TranscriptionStatuses.Queued,
        NormalizationComplete = false,
        QueuedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Submit a request for <paramref name="fileName"/> and wait until the service has run its completion check,
    /// which follows the transcription attempt and its temp-file cleanup on every path.
    /// </summary>
    public async Task SubmitAndWaitAsync(string fileName = DefaultFileName)
    {
        await _channel.SubmitAsync(new TranscriptionRequest
        {
            JobId = JobId,
            FeedId = "test-feed",
            FileName = fileName,
            EpisodeId = "ep-1",
        });

        await Jobs.WaitForProcessingCompleteAsync(JobId, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Wait until the started service's ExecuteAsync has returned. StartAsync does not run ExecuteAsync inline, so a test
    /// asserting on what ExecuteAsync did (or did not do) must wait for it first.
    /// </summary>
    public Task WaitForExecuteCompletionAsync() => _service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>
    /// Run the service's startup recovery scan directly, without starting the service.
    /// </summary>
    public Task RecoverAsync() => _service.RecoverInterruptedTranscriptionsAsync(CancellationToken.None);

    /// <summary>
    /// Take every request waiting in the channel. Only meaningful while the service is not started (nothing else reads it).
    /// </summary>
    public async Task<List<TranscriptionRequest>> DrainSubmittedAsync()
    {
        _channel.Complete();
        List<TranscriptionRequest> requests = [];
        await foreach (var request in _channel.ReadAllAsync(CancellationToken.None))
        {
            requests.Add(request);
        }

        return requests;
    }

    private TranscriptionBackgroundService BuildService(bool useFast)
    {
        var episodeService = new EpisodeService(Blob, NullLogger<EpisodeService>.Instance);
        var completionService = new JobCompletionService(
            Jobs,
            episodeService,
            Blob,
            new NullJobProgressChannel(),
            new NullFeedEventChannel(),
            new StubPushNotificationService(Blob, episodeService, Jobs),
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
            Speech,
            Blob,
            Jobs,
            new NullJobProgressChannel(),
            completionService,
            Duration,
            Converter,
            _lifetime,
            configuration,
            NullLogger<TranscriptionBackgroundService>.Instance);
    }

    public sealed class FakeSpeechTranscriptionService : ISpeechTranscriptionService
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

        public List<(string ContentType, string? FileName)> FastRequests { get; } = [];

        public List<string> SubmittedAudioUrls { get; } = [];

        public Task<string?> TranscribeFastAsync(Stream audio, string contentType, string? fileName, CancellationToken ct)
        {
            FastCallCount++;
            FastRequests.Add((contentType, fileName));

            if (FastThrows != null)
            {
                throw FastThrows;
            }

            return Task.FromResult(FastReturns);
        }

        public Task<string> SubmitAsync(string audioUrl, CancellationToken ct)
        {
            SubmitCallCount++;
            SubmittedAudioUrls.Add(audioUrl);

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

    public sealed class FakeAudioDurationProbe : IAudioDurationProbe
    {
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;

        public Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken ct) => Task.FromResult(Duration);
    }

    /// <summary>
    /// <see cref="IWavConverter"/> that records each conversion and writes a placeholder WAV instead of running FFmpeg.
    /// A configured <see cref="Throws"/> fails the conversion without writing output, as the real converter's contract promises.
    /// </summary>
    public sealed class FakeWavConverter : IWavConverter
    {
        public Exception? Throws { get; set; }

        public List<(string InputPath, string WavPath)> Conversions { get; } = [];

        public Task ConvertAsync(string inputPath, string wavPath, CancellationToken ct)
        {
            Conversions.Add((inputPath, wavPath));

            if (Throws != null)
            {
                throw Throws;
            }

            File.WriteAllBytes(wavPath, [0x52, 0x49, 0x46, 0x46]);

            return Task.CompletedTask;
        }
    }
}
