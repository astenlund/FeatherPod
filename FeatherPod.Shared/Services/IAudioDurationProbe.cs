namespace FeatherPod.Shared.Services;

/// <summary>
/// Resolves the playback duration of an audio file, either at a local path or at a URL that
/// supports HTTP range reads (such as a blob SAS URL), so the duration can be known before the
/// file is downloaded. Abstracted so the transcription pipeline can route Fast vs. batch by
/// duration without taking a hard dependency on the FFmpeg binary in tests.
/// </summary>
public interface IAudioDurationProbe
{
    Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken ct);

    /// <summary>
    /// Probe the audio at <paramref name="source"/> without downloading all of it. Only plain MP3,
    /// WAV, Ogg and FLAC content is read, so content that names other URLs (a playlist) fails the
    /// probe instead of making it fetch them. A source that stops sending data must fail the probe
    /// within a bounded time rather than hang it, so callers can fall back. A failure's exception
    /// text may contain the URL, including any SAS token it carries.
    /// </summary>
    Task<TimeSpan> GetDurationAsync(Uri source, CancellationToken ct);
}
