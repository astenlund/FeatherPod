using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Drives <see cref="FeatherPod.Server.Services.TranscriptionBackgroundService"/> on the batch path
/// (<c>UseFastTranscription=false</c>). Covers batch success and failure outcomes plus the
/// quota-cleanup DeleteAsync call in the finally block, and which audio a batch job submits: the
/// original pending blob, or for M4A a WAV conversion uploaded beside it.
/// </summary>
[Collection("Sequential")]
public class BatchTranscriptionTests : IAsyncLifetime
{
    private readonly TranscriptionTestHarness _harness = new(useFast: false);

    public Task InitializeAsync() => _harness.InitializeAsync();

    public Task DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task SucceededStatus_UploadsVttAndMarksCompleted()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/abc";
        _harness.Speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/abc/files", null);
        _harness.Speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:02.000\r\n<v Speaker 0>Hi.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Null(entity.TranscriptionError);
        Assert.Single(_harness.Blob.UploadedTranscripts);
        Assert.Equal("test-feed", _harness.Blob.UploadedTranscripts[0].FeedId);
        Assert.Equal("ep-1", _harness.Blob.UploadedTranscripts[0].EpisodeId);
        Assert.StartsWith("WEBVTT", _harness.Blob.UploadedTranscripts[0].Vtt);
        Assert.Contains("https://azure.example/transcriptions/abc", _harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task FailedStatus_MarksFailedWithErrorMessage_AndDeletes()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/def";
        _harness.Speech.PollReturns = ("Failed", null, "BadRequest: audio too short");

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("BadRequest: audio too short", entity.TranscriptionError);
        Assert.Empty(_harness.Blob.UploadedTranscripts);
        Assert.Contains("https://azure.example/transcriptions/def", _harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task GetResultReturnsNull_MarksFailedProducedNoOutput_AndDeletes()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/ghi";
        _harness.Speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/ghi/files", null);
        _harness.Speech.GetResultReturns = null;

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("Transcription produced no output", entity.TranscriptionError);
        Assert.Empty(_harness.Blob.UploadedTranscripts);
        Assert.Contains("https://azure.example/transcriptions/ghi", _harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task SubmitThrows_MarksFailed_DoesNotCallDelete()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Speech.SubmitThrows = new HttpRequestException("Speech API POST /speechtotext/v3.2/transcriptions failed (401): unauthorized");

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.StartsWith("Speech API POST /speechtotext/v3.2/transcriptions failed", entity.TranscriptionError);
        Assert.Empty(_harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task PollThrows_MarksFailed_AndStillCallsDelete()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/jkl";
        _harness.Speech.PollThrows = new TimeoutException("Batch transcription timed out");

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("Batch transcription timed out", entity.TranscriptionError);
        Assert.Contains("https://azure.example/transcriptions/jkl", _harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task M4a_DownloadsConvertsAndSubmitsUploadedWav()
    {
        // Arrange
        const string m4aFileName = "episode.m4a";
        var wavBlobName = $"_transcription-{TranscriptionTestHarness.JobId}.wav";
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity(m4aFileName));
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/m4a";
        _harness.Speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/m4a/files", null);
        _harness.Speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:02.000\r\n<v Speaker 0>Hi.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync(m4aFileName);

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Equal([m4aFileName], _harness.Blob.PendingDownloads);
        var conversion = Assert.Single(_harness.Converter.Conversions);
        Assert.Equal(TranscriptionTestHarness.TempInputPath(m4aFileName), conversion.InputPath);
        Assert.Equal(TranscriptionTestHarness.TempWavPath, conversion.WavPath);
        Assert.Equal([(wavBlobName, true)], _harness.Blob.PendingAudioUploads);
        Assert.Equal([wavBlobName], _harness.Blob.SasRequests);
        Assert.Equal([RecordingBlobService.SasUrlFor(wavBlobName)], _harness.Speech.SubmittedAudioUrls);
        Assert.False(File.Exists(TranscriptionTestHarness.TempInputPath(m4aFileName)));
        Assert.False(File.Exists(TranscriptionTestHarness.TempWavPath));
    }

    [Fact]
    public async Task Mp3_SubmitsOriginalPendingBlobWithoutDownloadOrConversion()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/mp3";
        _harness.Speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/mp3/files", null);
        _harness.Speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:02.000\r\n<v Speaker 0>Hi.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        Assert.Empty(_harness.Blob.PendingDownloads);
        Assert.Empty(_harness.Converter.Conversions);
        Assert.Empty(_harness.Blob.PendingAudioUploads);
        Assert.Equal([TranscriptionTestHarness.DefaultFileName], _harness.Blob.SasRequests);
        Assert.Equal([RecordingBlobService.SasUrlFor(TranscriptionTestHarness.DefaultFileName)], _harness.Speech.SubmittedAudioUrls);
    }
}
