using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Transcription runs and job attempts: a still-live run of an earlier attempt changes nothing on a retried job, runs of different
/// attempts never share temp files, and a transcript retry run reads the published episode audio and only stages its transcript.
/// </summary>
[Collection("Sequential")]
public sealed class TranscriptionAttemptTests : IAsyncLifetime
{
    private readonly TranscriptionTestHarness _harness = new(useFast: true);

    public Task InitializeAsync() => _harness.InitializeAsync();

    public Task DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task RunOfEarlierAttempt_WritesNoTranscriptAndNoStatus()
    {
        // Arrange: the job was reset to attempt 2; the request belongs to attempt 1
        var entity = TranscriptionTestHarness.CreateEntity();
        entity.Attempt = 2;
        entity.TranscriptionStatus = TranscriptionStatuses.Queued;
        _harness.Jobs.SetEntity(entity);
        _harness.Speech.FastReturns = "WEBVTT\n\nstale attempt";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        Assert.Empty(_harness.Blob.UploadedTranscripts);
        Assert.Equal(TranscriptionStatuses.Queued, _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!.TranscriptionStatus);
    }

    [Fact]
    public async Task RunWhoseTrackWasEndedMeanwhile_LeavesTheEndedOutcome()
    {
        // Arrange: the recurring recovery failed the stalled track before this late run reports
        var entity = TranscriptionTestHarness.CreateEntity();
        entity.TranscriptionStatus = TranscriptionStatuses.Failed;
        entity.TranscriptionError = "stalled";
        _harness.Jobs.SetEntity(entity);
        _harness.Speech.FastReturns = "WEBVTT\n\nlate";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var job = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, job.TranscriptionStatus);
        Assert.Equal("stalled", job.TranscriptionError);
    }

    [Fact]
    public async Task RunOfLaterAttempt_UsesTempFilesAndStagedTranscriptOfItsOwnAttempt()
    {
        // Arrange
        var entity = TranscriptionTestHarness.CreateEntity("episode.m4a");
        entity.Attempt = 2;
        _harness.Jobs.SetEntity(entity);
        _harness.Speech.FastReturns = "WEBVTT\n\nattempt two";

        // Act
        await _harness.SubmitAndWaitAsync("episode.m4a", r => r with { Attempt = 2 });

        // Assert
        var conversion = Assert.Single(_harness.Converter.Conversions);
        Assert.EndsWith($"transcribe-{TranscriptionTestHarness.JobId}-a2.m4a", conversion.InputPath);
        Assert.EndsWith($"transcribe-{TranscriptionTestHarness.JobId}-a2.wav", conversion.WavPath);
        var staged = Assert.Single(_harness.Blob.StagedTranscripts);
        Assert.Equal(2, staged.Attempt);
        Assert.Equal(TranscriptionStatuses.Completed, _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!.TranscriptionStatus);
    }

    [Fact]
    public async Task TranscriptRetryRun_ReadsEpisodeAudioAndOnlyStagesItsTranscript()
    {
        // Arrange
        _harness.Jobs.SetEntity(NewTranscriptRetryEntity());
        _harness.Speech.FastReturns = "WEBVTT\n\nretried";

        // Act
        await _harness.SubmitAndWaitAsync(configure: r => r with { FromEpisodeAudio = true, Attempt = 1 });

        // Assert
        Assert.NotEmpty(_harness.Blob.AudioSasRequests.Concat(_harness.Blob.AudioDownloads));
        Assert.Empty(_harness.Blob.PendingDownloads);
        Assert.Empty(_harness.Blob.SasRequests);
        Assert.Single(_harness.Blob.StagedTranscripts);
        Assert.Empty(_harness.Blob.UploadedTranscripts);
    }

    [Fact]
    public async Task TranscriptRetryRun_SpeechThrows_JobEndsCompletedWithFailedTranscript()
    {
        // Arrange
        _harness.Jobs.SetEntity(NewTranscriptRetryEntity());
        _harness.Speech.FastThrows = new InvalidOperationException("Speech is down");

        // Act
        await _harness.SubmitAndWaitAsync(configure: r => r with { FromEpisodeAudio = true, Attempt = 1 });
        var terminal = await WaitForTerminalAsync();

        // Assert
        var job = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.True(terminal, "the retry job's join did not end it");
        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(TranscriptionStatuses.Failed, job.TranscriptionStatus);
        Assert.Empty(_harness.Blob.UploadedTranscripts);
    }

    /// <summary>
    /// The harness's completion signal fires when the join starts reading the job, so wait, with a timeout, for the join's terminal write.
    /// </summary>
    private async Task<bool> WaitForTerminalAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)?.GetJobStatus().IsTerminal() == true)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    private static JobStatusEntity NewTranscriptRetryEntity()
    {
        var entity = TranscriptionTestHarness.CreateEntity();
        entity.Kind = JobKinds.TranscriptRetry;
        entity.Attempt = 1;
        entity.EpisodeId = "ep-1";
        entity.NormalizationComplete = true;
        entity.Status = nameof(JobStatus.Queued);
        entity.EpisodeUploadedAt = DateTimeOffset.UtcNow.AddDays(-1);

        return entity;
    }
}

/// <summary>
/// Startup recovery of transcript retry jobs: an interrupted retry is resubmitted against the episode audio for its attempt.
/// </summary>
[Collection("Sequential")]
public sealed class TranscriptRetryRecoveryTests : IAsyncDisposable
{
    private readonly TranscriptionTestHarness _harness = new(useFast: true);

    public async ValueTask DisposeAsync() => await _harness.DisposeAsync();

    [Fact]
    public async Task StartupRecovery_InterruptedTranscriptRetry_ResubmittedFromEpisodeAudio()
    {
        // Arrange: a retry job a previous process left running
        var entity = TranscriptionTestHarness.CreateEntity();
        entity.Kind = JobKinds.TranscriptRetry;
        entity.Attempt = 1;
        entity.EpisodeId = "ep-1";
        entity.NormalizationComplete = true;
        entity.Status = nameof(JobStatus.Queued);
        entity.TranscriptionStatus = TranscriptionStatuses.Running;
        entity.QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        _harness.Jobs.SetEntity(entity);

        // Act
        await _harness.RecoverAsync();
        var submitted = await _harness.DrainSubmittedAsync();

        // Assert
        var request = Assert.Single(submitted);
        Assert.True(request.FromEpisodeAudio);
        Assert.Equal(1, request.Attempt);
    }
}
