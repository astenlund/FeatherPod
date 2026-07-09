using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
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

    public Task InitializeAsync() => _service.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        _channel.Complete();
        await _service.StopAsync(CancellationToken.None);
        _lifetime.Dispose();
    }

    public static JobStatusEntity CreateEntity() => new()
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

    public async Task SubmitAndWaitAsync()
    {
        await _channel.SubmitAsync(new TranscriptionRequest
        {
            JobId = JobId,
            FeedId = "test-feed",
            FileName = "episode.mp3",
            EpisodeId = "ep-1",
        });

        await Jobs.WaitForProcessingCompleteAsync(JobId, TimeSpan.FromSeconds(5));
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

    public sealed class FakeAudioDurationProbe : IAudioDurationProbe
    {
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;

        public Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken ct) => Task.FromResult(Duration);
    }
}
