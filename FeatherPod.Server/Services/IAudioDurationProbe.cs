namespace FeatherPod.Server.Services;

/// <summary>
/// Resolves the playback duration of an audio file at a local path. Abstracted so the
/// transcription pipeline can route Fast vs. batch by duration without taking a hard
/// dependency on the FFmpeg binary in tests.
/// </summary>
public interface IAudioDurationProbe
{
    Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken ct);
}
