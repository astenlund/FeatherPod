using FeatherPod.Server.Services;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Drives <see cref="TranscriptionBackgroundService"/> on the Fast path
/// (<c>UseFastTranscription=true</c>). Covers Fast success and failure outcomes, the
/// Fast-to-batch fallback when the endpoint is unavailable, and the duration cap that skips Fast,
/// for both directly accepted audio and M4A, which the service converts to WAV first. MP3/WAV/OGG/FLAC
/// are probed by SAS URL before any download, falling back to a local probe; other unconverted names
/// keep the download-then-probe order.
/// </summary>
[Collection("Sequential")]
public class FastTranscriptionTests : IAsyncLifetime
{
    private const string M4aFileName = "episode.m4a";

    private readonly TranscriptionTestHarness _harness = new(useFast: true);

    public Task InitializeAsync() => _harness.InitializeAsync();

    public Task DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task FastSucceeded_UploadsVttAndMarksCompleted_NoBatchCalls()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Duration.Duration = TimeSpan.FromMinutes(20);
        _harness.Speech.FastReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Fast.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Null(entity.TranscriptionError);
        Assert.StartsWith("WEBVTT", _harness.Blob.StagedTranscripts[0].Vtt);
        Assert.Equal(1, _harness.Speech.FastCallCount);
        Assert.Equal(0, _harness.Speech.SubmitCallCount);
        Assert.Empty(_harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task FastReturnsNull_MarksFailedNoOutput_NoBatchFallback()
    {
        // Arrange -- null is "ran fine but said nothing", not "endpoint unavailable" -- so no batch retry.
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Duration.Duration = TimeSpan.FromMinutes(5);
        _harness.Speech.FastReturns = null;

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("Transcription produced no output", entity.TranscriptionError);
        Assert.Empty(_harness.Blob.StagedTranscripts);
        Assert.Equal(1, _harness.Speech.FastCallCount);
        Assert.Equal(0, _harness.Speech.SubmitCallCount);
    }

    [Fact]
    public async Task FastThrowsGenericException_MarksFailed_NoBatchFallback()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Duration.Duration = TimeSpan.FromMinutes(5);
        _harness.Speech.FastThrows = new HttpRequestException("Speech API POST /speechtotext/transcriptions:transcribe failed (500): boom");

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.StartsWith("Speech API POST /speechtotext/transcriptions:transcribe", entity.TranscriptionError);
        Assert.Equal(1, _harness.Speech.FastCallCount);
        Assert.Equal(0, _harness.Speech.SubmitCallCount);
        Assert.Empty(_harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task FastUnavailable_FallsBackToBatchSucceeded()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Duration.Duration = TimeSpan.FromMinutes(5);
        _harness.Speech.FastThrows = new FastTranscriptionUnavailableException("AudioTooLong");
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/fallback";
        _harness.Speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/fallback/files", null);
        _harness.Speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:02.000\r\n<v Speaker 0>Batch.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Null(entity.TranscriptionError);
        Assert.StartsWith("WEBVTT", _harness.Blob.StagedTranscripts[0].Vtt);
        Assert.Contains("Batch", _harness.Blob.StagedTranscripts[0].Vtt);
        Assert.Equal(1, _harness.Speech.FastCallCount);
        Assert.Contains("https://azure.example/transcriptions/fallback", _harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task Mp3OverFastCap_ProbesByUrlAndBatchesWithoutDownload()
    {
        // Arrange -- 200 minutes is > the 110-minute Fast cap.
        var sasUrl = RecordingBlobService.SasUrlFor(TranscriptionTestHarness.DefaultFileName);
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Duration.Duration = TimeSpan.FromMinutes(200);
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/long";
        _harness.Speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/long/files", null);
        _harness.Speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Long.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Equal([new Uri(sasUrl)], _harness.Duration.UrlProbes);
        Assert.Empty(_harness.Duration.PathProbes);
        Assert.Empty(_harness.Blob.PendingDownloads);
        Assert.Equal(0, _harness.Speech.FastCallCount);
        Assert.Equal([sasUrl], _harness.Speech.SubmittedAudioUrls);
        Assert.Contains("https://azure.example/transcriptions/long", _harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task Mp3UnderFastCap_ProbesByUrlThenDownloadsOnceForFast()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Duration.Duration = TimeSpan.FromMinutes(20);
        _harness.Speech.FastReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Fast.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Equal([new Uri(RecordingBlobService.SasUrlFor(TranscriptionTestHarness.DefaultFileName))], _harness.Duration.UrlProbes);
        Assert.Empty(_harness.Duration.PathProbes);
        Assert.Equal([TranscriptionTestHarness.DefaultFileName], _harness.Blob.PendingDownloads);
        var fastRequest = Assert.Single(_harness.Speech.FastRequests);
        Assert.Equal(AudioHelper.GetMimeType(TranscriptionTestHarness.DefaultFileName), fastRequest.ContentType);
        Assert.Equal(0, _harness.Speech.SubmitCallCount);
        Assert.False(File.Exists(TranscriptionTestHarness.TempInputPath(TranscriptionTestHarness.DefaultFileName)));
    }

    [Fact]
    public async Task OtherFormatUnderFastCap_DownloadsAndProbesLocallyWithoutUrlProbe()
    {
        // Arrange -- only MP3/WAV/OGG/FLAC are probed by URL; any other unconverted name keeps the download-first order.
        const string aacFileName = "episode.aac";
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity(aacFileName));
        _harness.Duration.Duration = TimeSpan.FromMinutes(20);
        _harness.Speech.FastReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Fast.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync(aacFileName);

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Empty(_harness.Duration.UrlProbes);
        Assert.Equal([TranscriptionTestHarness.TempInputPath(aacFileName)], _harness.Duration.PathProbes);
        Assert.Equal([aacFileName], _harness.Blob.PendingDownloads);
        Assert.Empty(_harness.Blob.SasRequests);
        Assert.Equal(1, _harness.Speech.FastCallCount);
    }

    [Fact]
    public async Task UrlProbeFails_DownloadsAndProbesLocally_WithoutLoggingTheSasUrl()
    {
        // Arrange -- ffprobe's error text echoes its input, so a failure can carry the SAS token.
        const string secret = "sig=SECRET-TOKEN";
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity());
        _harness.Duration.Duration = TimeSpan.FromMinutes(20);
        _harness.Duration.UrlThrows = new InvalidOperationException($"ffprobe exited with code 1: https://blob.example/pending/episode.mp3?{secret}: Server returned 403");
        _harness.Speech.FastReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Fast.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync();

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Single(_harness.Duration.UrlProbes);
        Assert.Equal([TranscriptionTestHarness.TempInputPath(TranscriptionTestHarness.DefaultFileName)], _harness.Duration.PathProbes);
        Assert.Equal([TranscriptionTestHarness.DefaultFileName], _harness.Blob.PendingDownloads);
        Assert.Equal(1, _harness.Speech.FastCallCount);
        var warning = Assert.Single(_harness.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(TranscriptionTestHarness.DefaultFileName, warning.Message);
        Assert.DoesNotContain(_harness.Logger.Entries, e => e.Text.Contains(secret, StringComparison.Ordinal) || e.Text.Contains("blob.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task M4aUnderFastCap_ConvertsOnceAndSendsWavToFast()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity(M4aFileName));
        _harness.Duration.Duration = TimeSpan.FromMinutes(20);
        _harness.Speech.FastReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Fast.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync(M4aFileName);

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Equal([M4aFileName], _harness.Blob.PendingDownloads);
        Assert.Equal([TranscriptionTestHarness.TempWavPath], _harness.Duration.PathProbes);
        Assert.Empty(_harness.Duration.UrlProbes);
        var conversion = Assert.Single(_harness.Converter.Conversions);
        Assert.Equal(TranscriptionTestHarness.TempInputPath(M4aFileName), conversion.InputPath);
        Assert.Equal(TranscriptionTestHarness.TempWavPath, conversion.WavPath);
        var fastRequest = Assert.Single(_harness.Speech.FastRequests);
        Assert.Equal(AudioHelper.GetMimeType(".wav"), fastRequest.ContentType);
        Assert.Equal(Path.GetFileName(TranscriptionTestHarness.TempWavPath), fastRequest.FileName);
        Assert.Empty(_harness.Blob.PendingAudioUploads);
        Assert.Equal(0, _harness.Speech.SubmitCallCount);
        AssertTempFilesDeleted();
    }

    [Fact]
    public async Task M4aFastUnavailable_FallsBackToBatchReusingDownloadAndConvertedWav()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity(M4aFileName));
        _harness.Duration.Duration = TimeSpan.FromMinutes(5);
        _harness.Speech.FastThrows = new FastTranscriptionUnavailableException("InvalidAudioFormat");
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/m4a-fallback";
        _harness.Speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/m4a-fallback/files", null);
        _harness.Speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:02.000\r\n<v Speaker 0>Batch.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync(M4aFileName);

        // Assert -- the fallback reuses the Fast attempt's download and WAV rather than redoing either.
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Equal(1, _harness.Speech.FastCallCount);
        Assert.Equal([M4aFileName], _harness.Blob.PendingDownloads);
        Assert.Single(_harness.Converter.Conversions);
        AssertBatchSubmittedConvertedWav();
        Assert.Contains("https://azure.example/transcriptions/m4a-fallback", _harness.Speech.DeletedUrls);
        AssertTempFilesDeleted();
    }

    [Fact]
    public async Task M4aOverFastCap_UsesBatchWithSingleConversion()
    {
        // Arrange -- 200 minutes is > the 110-minute Fast cap.
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity(M4aFileName));
        _harness.Duration.Duration = TimeSpan.FromMinutes(200);
        _harness.Speech.SubmitReturns = "https://azure.example/transcriptions/m4a-long";
        _harness.Speech.PollReturns = ("Succeeded", "https://azure.example/transcriptions/m4a-long/files", null);
        _harness.Speech.GetResultReturns = "WEBVTT\r\n\r\n00:00:00.000 --> 00:00:01.000\r\n<v Speaker 0>Long.</v>\r\n";

        // Act
        await _harness.SubmitAndWaitAsync(M4aFileName);

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Completed, entity.TranscriptionStatus);
        Assert.Equal(0, _harness.Speech.FastCallCount);
        Assert.Equal([M4aFileName], _harness.Blob.PendingDownloads);
        Assert.Single(_harness.Converter.Conversions);
        AssertBatchSubmittedConvertedWav();
        AssertTempFilesDeleted();
    }

    [Fact]
    public async Task M4aConversionFails_MarksFailedAndDeletesTempInput()
    {
        // Arrange
        _harness.Jobs.SetEntity(TranscriptionTestHarness.CreateEntity(M4aFileName));
        _harness.Duration.Duration = TimeSpan.FromMinutes(5);
        _harness.Converter.Throws = new InvalidOperationException("FFmpeg is not available for WAV conversion");

        // Act -- SubmitAndWaitAsync returns only once the completion check has run after the failure.
        await _harness.SubmitAndWaitAsync(M4aFileName);

        // Assert
        var entity = _harness.Jobs.GetEntity(TranscriptionTestHarness.JobId)!;
        Assert.Equal(TranscriptionStatuses.Failed, entity.TranscriptionStatus);
        Assert.Equal("FFmpeg is not available for WAV conversion", entity.TranscriptionError);
        Assert.Single(_harness.Converter.Conversions);
        Assert.Equal(0, _harness.Speech.FastCallCount);
        Assert.Equal(0, _harness.Speech.SubmitCallCount);
        Assert.Empty(_harness.Blob.StagedTranscripts);
        AssertTempFilesDeleted();
    }

    private void AssertBatchSubmittedConvertedWav()
    {
        var wavBlobName = $"_transcription-{TranscriptionTestHarness.JobId}-a1.wav";
        Assert.Equal([(wavBlobName, true)], _harness.Blob.PendingAudioUploads);
        Assert.Equal([wavBlobName], _harness.Blob.SasRequests);
        Assert.Equal([RecordingBlobService.SasUrlFor(wavBlobName)], _harness.Speech.SubmittedAudioUrls);
    }

    private static void AssertTempFilesDeleted()
    {
        Assert.False(File.Exists(TranscriptionTestHarness.TempInputPath(M4aFileName)));
        Assert.False(File.Exists(TranscriptionTestHarness.TempWavPath));
    }
}
