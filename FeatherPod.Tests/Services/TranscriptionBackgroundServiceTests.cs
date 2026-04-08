using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Drives <see cref="TranscriptionBackgroundService"/> via a fake <see cref="ISpeechTranscriptionService"/>
/// and the real <see cref="ITranscriptionChannel"/>. Covers the four transcription outcomes
/// and verifies the quota-cleanup DeleteAsync call in the finally block.
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
    private readonly TranscriptionBackgroundService _service;

    public TranscriptionBackgroundServiceTests()
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureSpeech:MaxConcurrent"] = "1" })
            .Build();

        _service = new TranscriptionBackgroundService(
            _channel,
            _speech,
            _blob,
            _jobs,
            new NullJobProgressChannel(),
            completionService,
            _lifetime,
            configuration,
            NullLogger<TranscriptionBackgroundService>.Instance);
    }

    public Task InitializeAsync() => _service.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        _channel.Complete();
        await _service.StopAsync(CancellationToken.None);
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

    private sealed class FakeSpeechTranscriptionService : ISpeechTranscriptionService
    {
        public bool IsAvailable { get; set; } = true;

        public string SubmitReturns { get; set; } = string.Empty;

        public Exception? SubmitThrows { get; set; }

        public (string Status, string? FilesListUrl, string? ErrorMessage) PollReturns { get; set; }

        public Exception? PollThrows { get; set; }

        public string? GetResultReturns { get; set; }

        public List<string> DeletedUrls { get; } = [];

        public Task<string> SubmitAsync(string audioUrl, CancellationToken ct)
        {
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
}
