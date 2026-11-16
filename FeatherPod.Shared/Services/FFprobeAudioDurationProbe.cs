using FFMpegCore;

namespace FeatherPod.Shared.Services;

/// <summary>
/// <see cref="IAudioDurationProbe"/> backed by FFMpegCore's <see cref="FFProbe"/>. Ensures the
/// ffmpeg/ffprobe binaries through <see cref="FFmpegBinaryManager"/> first, which also points
/// FFMpegCore at a locally downloaded copy.
/// </summary>
public sealed class FFprobeAudioDurationProbe : IAudioDurationProbe
{
    /// <summary>
    /// How long one network read of a URL probe may wait for data. ffprobe's HTTP reads have no timeout by default, so a
    /// connection that is accepted and then goes silent would otherwise hold the probe, and its caller, indefinitely.
    /// </summary>
    internal static readonly TimeSpan DefaultUrlReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The only demuxers a URL probe may use. Uploaded content is untrusted, and without this list ffprobe picks a demuxer from
    /// the content and name, so a playlist-type file (HLS, DASH, concat) would make it fetch the URLs the file names, from this
    /// server's network position. A local-file probe is already confined by ffmpeg's file-only protocol whitelist.
    /// </summary>
    private const string UrlFormatWhitelist = "mp3,wav,ogg,flac";

    private readonly FFmpegBinaryManager _binaryManager;
    private readonly TimeSpan _urlReadTimeout;

    public FFprobeAudioDurationProbe(FFmpegBinaryManager binaryManager)
        : this(binaryManager, DefaultUrlReadTimeout)
    {
    }

    internal FFprobeAudioDurationProbe(FFmpegBinaryManager binaryManager, TimeSpan urlReadTimeout)
    {
        _binaryManager = binaryManager;
        _urlReadTimeout = urlReadTimeout;
    }

    public async Task<TimeSpan> GetDurationAsync(string filePath, CancellationToken ct)
    {
        await EnsureFFprobeAsync(ct);
        var analysis = await FFProbe.AnalyseAsync(filePath, cancellationToken: ct);

        return RequireDuration(analysis, Path.GetFileName(filePath));
    }

    public async Task<TimeSpan> GetDurationAsync(Uri source, CancellationToken ct)
    {
        await EnsureFFprobeAsync(ct);

        // rw_timeout is in microseconds; on expiry ffprobe exits non-zero, which FFMpegCore raises as an exception, as it does for
        // content outside the format whitelist.
        var readTimeoutMicroseconds = (long)_urlReadTimeout.TotalMicroseconds;
        var customArguments = $"-rw_timeout {readTimeoutMicroseconds} -format_whitelist {UrlFormatWhitelist}";
        var analysis = await FFProbe.AnalyseAsync(source, cancellationToken: ct, customArguments: customArguments);

        // Name the file from the path alone: the query string can hold a SAS token.
        return RequireDuration(analysis, Path.GetFileName(Uri.UnescapeDataString(source.AbsolutePath)));
    }

    private async Task EnsureFFprobeAsync(CancellationToken ct)
    {
        if (!await _binaryManager.EnsureFFmpegAvailableAsync(ct))
        {
            throw new InvalidOperationException("FFmpeg is not available for probing audio duration");
        }
    }

    /// <summary>
    /// FFMpegCore reports a missing or unparseable duration as zero, which would silently route
    /// the file down the Fast path; treat it as a probe failure instead.
    /// </summary>
    private static TimeSpan RequireDuration(IMediaAnalysis analysis, string fileName)
    {
        if (analysis.Duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"ffprobe reported no duration for {fileName}");
        }

        return analysis.Duration;
    }
}
