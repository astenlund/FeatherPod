using System.Net;
using System.Net.Sockets;
using FeatherPod.Shared.Services;
using FFMpegCore;
using FFMpegCore.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatherPod.Tests;

/// <summary>
/// Runs the real ffprobe against audio served over HTTP, as the transcription service probes a pending blob by SAS URL.
/// The served URL carries a SAS-shaped query string, and the server answers 403 unless every parameter arrives intact, as
/// Blob Storage does, so the probe must pass the whole URL, ampersand included, to ffprobe.
/// </summary>
public sealed class FFprobeAudioDurationProbeTests : IAsyncLifetime
{
    private const string AudioFileName = "tone.flac";

    private const string SasVersion = "2025-01-05";

    private const string SasSignature = "not-a-real-signature";

    /// <summary>
    /// A SAS-shaped query whose second parameter follows an ampersand, so a URL cut at the ampersand loses the signature.
    /// </summary>
    private const string SasQuery = $"?sv={SasVersion}&sig={SasSignature}";

    private static readonly TimeSpan StalledProbeWaitLimit = TimeSpan.FromSeconds(60);

    private readonly string _workDir = Path.Combine(Path.GetTempPath(), "FeatherPod.Tests", $"probe-{Guid.NewGuid():N}");
    private readonly FFmpegBinaryManager _binaryManager = new();
    private WebApplication? _server;
    private Uri? _baseUri;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workDir);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup: throwing here would mask the test's real failure.
        }
    }

    [FFmpegFact]
    public async Task GetDurationAsync_Url_ReadsDurationOverHttp()
    {
        // Arrange
        await GenerateToneAsync(seconds: 3);
        await StartServerAsync();
        var probe = new FFprobeAudioDurationProbe(_binaryManager);
        var source = new Uri(_baseUri!, $"pending/{AudioFileName}{SasQuery}");

        // Act
        var duration = await probe.GetDurationAsync(source, CancellationToken.None);

        // Assert
        Assert.InRange(duration, TimeSpan.FromSeconds(2.9), TimeSpan.FromSeconds(3.1));
    }

    [FFmpegFact]
    public async Task GetDurationAsync_UrlNotFound_ThrowsWithServerStatus()
    {
        // Arrange
        await StartServerAsync();
        var probe = new FFprobeAudioDurationProbe(_binaryManager);
        var source = new Uri(_baseUri!, $"pending/missing.flac{SasQuery}");

        // Act
        var exception = await Record.ExceptionAsync(() => probe.GetDurationAsync(source, CancellationToken.None));

        // Assert
        Assert.NotNull(exception);
        Assert.Contains("404", exception.Message, StringComparison.Ordinal);
    }

    [FFmpegFact]
    public async Task GetDurationAsync_UrlServerNeverResponds_FailsInsteadOfHanging()
    {
        // Arrange -- a server that accepts the request and then sends nothing, as a stalled Blob Storage connection would.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync();
        var probe = new FFprobeAudioDurationProbe(_binaryManager, urlReadTimeout: TimeSpan.FromSeconds(2));
        var source = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/pending/{AudioFileName}{SasQuery}");

        // Act
        Exception? exception;
        try
        {
            exception = await Record.ExceptionAsync(() => probe.GetDurationAsync(source, CancellationToken.None).WaitAsync(StalledProbeWaitLimit));
        }
        finally
        {
            // Closing the held connection lets a hung ffprobe exit, so a failing run leaves no process behind.
            if (accepted.IsCompletedSuccessfully)
            {
                accepted.Result.Dispose();
            }
        }

        // Assert -- ffprobe itself must give up; a TimeoutException would mean the wait limit, not the probe, ended it.
        Assert.IsType<FFMpegException>(exception);
    }

    [FFmpegFact]
    public async Task GetDurationAsync_UrlServesPlaylist_FailsWithoutFetchingUrlsItNames()
    {
        // Arrange -- an uploaded HLS playlist naming a segment on another host, which an unrestricted probe would request.
        using var otherHost = new TcpListener(IPAddress.Loopback, 0);
        otherHost.Start();
        var otherPort = ((IPEndPoint)otherHost.LocalEndpoint).Port;
        const string playlistName = "evil.m3u8";
        await File.WriteAllTextAsync(
            Path.Combine(_workDir, playlistName),
            $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXTINF:10.0,\nhttp://127.0.0.1:{otherPort}/internal/segment0.ts\n#EXT-X-ENDLIST\n");
        await StartServerAsync();
        var probe = new FFprobeAudioDurationProbe(_binaryManager);
        var source = new Uri(_baseUri!, $"pending/{playlistName}{SasQuery}");

        // Act
        var exception = await Record.ExceptionAsync(() => probe.GetDurationAsync(source, CancellationToken.None));

        // Assert -- the probe fails (the caller then probes a downloaded copy) and the host named in the file got nothing.
        Assert.IsType<FFMpegException>(exception);
        Assert.False(otherHost.Pending());
    }

    private async Task GenerateToneAsync(int seconds)
    {
        Assert.True(await _binaryManager.EnsureFFmpegAvailableAsync());

        await FFMpegArguments
            .FromFileInput($"sine=frequency=440:duration={seconds}", verifyExists: false, options => options.ForceFormat("lavfi"))
            .OutputToFile(Path.Combine(_workDir, AudioFileName), overwrite: true, options => options.WithCustomArgument("-ac 1 -ar 16000 -c:a flac"))
            .ProcessAsynchronously();
    }

    /// <summary>
    /// Serve the work directory under <c>/pending/</c> with byte-range support, as Blob Storage serves a SAS URL, answering 403
    /// to a request whose query lacks either SAS parameter. The content root is the work directory so the server does not load
    /// the test output's copied appsettings.json, whose AllowedHosts admits only the Azure hostnames and would answer 400 to a
    /// loopback request.
    /// </summary>
    private async Task StartServerAsync()
    {
        _server = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = _workDir }).Build();
        _server.Urls.Add("http://127.0.0.1:0");
        _server.MapGet("/pending/{fileName}", (string fileName, HttpRequest request) =>
        {
            if (request.Query["sv"] != SasVersion || request.Query["sig"] != SasSignature)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var path = Path.Combine(_workDir, Path.GetFileName(fileName));

            return File.Exists(path) ? Results.File(path, "audio/flac", enableRangeProcessing: true) : Results.NotFound();
        });

        await _server.StartAsync();
        _baseUri = new Uri(_server.Urls.Single() + "/");
    }
}
