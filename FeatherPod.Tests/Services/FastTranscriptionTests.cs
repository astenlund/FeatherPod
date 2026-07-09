using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;
using FeatherPod.Tests.Helpers;

namespace FeatherPod.Tests.Services;

/// <summary>
/// Drives <see cref="TranscriptionBackgroundService"/> on the Fast path
/// (<c>UseFastTranscription=true</c>). Covers Fast success and failure outcomes, the
/// Fast-to-batch fallback when the endpoint is unavailable, and the duration cap that skips Fast.
/// </summary>
[Collection("Sequential")]
public class FastTranscriptionTests : IAsyncLifetime
{
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
        Assert.StartsWith("WEBVTT", _harness.Blob.UploadedTranscripts[0].Vtt);
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
        Assert.Empty(_harness.Blob.UploadedTranscripts);
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
        Assert.StartsWith("WEBVTT", _harness.Blob.UploadedTranscripts[0].Vtt);
        Assert.Contains("Batch", _harness.Blob.UploadedTranscripts[0].Vtt);
        Assert.Equal(1, _harness.Speech.FastCallCount);
        Assert.Contains("https://azure.example/transcriptions/fallback", _harness.Speech.DeletedUrls);
    }

    [Fact]
    public async Task DurationOverFastCap_SkipsFastAndUsesBatch()
    {
        // Arrange -- 200 minutes is > the 110-minute Fast cap.
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
        Assert.Equal(0, _harness.Speech.FastCallCount);
        Assert.Contains("https://azure.example/transcriptions/long", _harness.Speech.DeletedUrls);
    }
}
