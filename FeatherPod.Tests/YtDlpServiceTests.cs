using FeatherPod.Shared.Models;
using FeatherPod.Shared.Services;

namespace FeatherPod.Tests;

public class YtDlpServiceTests
{
    public class DownloadArgsBuilding
    {
        [Fact]
        public void Video_WithFfmpegDir_IncludesFfmpegLocation()
        {
            // Arrange
            var ffmpegDir = "/home/user/.featherpod/ffmpeg";

            // Act
            var args = YtDlpService.BuildDownloadArgs(
                url: "https://www.youtube.com/watch?v=abc123",
                outputTemplate: "/tmp/abc123.%(ext)s",
                format: YouTubeFormat.Video,
                cookieFilePath: null,
                denoArgs: "",
                ffmpegDir: ffmpegDir);

            // Assert
            Assert.Contains($"--ffmpeg-location \"{ffmpegDir}\"", args);
        }

        [Fact]
        public void Audio_WithFfmpegDir_IncludesFfmpegLocation()
        {
            // Arrange
            var ffmpegDir = "/home/user/.featherpod/ffmpeg";

            // Act
            var args = YtDlpService.BuildDownloadArgs(
                url: "https://www.youtube.com/watch?v=abc123",
                outputTemplate: "/tmp/abc123.%(ext)s",
                format: YouTubeFormat.Audio,
                cookieFilePath: null,
                denoArgs: "",
                ffmpegDir: ffmpegDir);

            // Assert
            Assert.Contains($"--ffmpeg-location \"{ffmpegDir}\"", args);
        }

        [Fact]
        public void WithoutFfmpegDir_OmitsFfmpegLocation()
        {
            // Act
            var args = YtDlpService.BuildDownloadArgs(
                url: "https://www.youtube.com/watch?v=abc123",
                outputTemplate: "/tmp/abc123.%(ext)s",
                format: YouTubeFormat.Video,
                cookieFilePath: null,
                denoArgs: "",
                ffmpegDir: null);

            // Assert
            Assert.DoesNotContain("--ffmpeg-location", args);
        }

        [Fact]
        public void Video_IncludesMergeOutputFormat()
        {
            // Act
            var args = YtDlpService.BuildDownloadArgs(
                url: "https://www.youtube.com/watch?v=abc123",
                outputTemplate: "/tmp/abc123.%(ext)s",
                format: YouTubeFormat.Video,
                cookieFilePath: null,
                denoArgs: "",
                ffmpegDir: null);

            // Assert
            Assert.Contains("--merge-output-format mp4", args);
        }

        [Fact]
        public void Audio_IncludesExtractAudio()
        {
            // Act
            var args = YtDlpService.BuildDownloadArgs(
                url: "https://www.youtube.com/watch?v=abc123",
                outputTemplate: "/tmp/abc123.%(ext)s",
                format: YouTubeFormat.Audio,
                cookieFilePath: null,
                denoArgs: "",
                ffmpegDir: null);

            // Assert
            Assert.Contains("--extract-audio --audio-format m4a", args);
        }

        [Fact]
        public void WithCookies_IncludesCookieArgs()
        {
            // Arrange
            var cookiePath = "/tmp/cookies.txt";

            // Act
            var args = YtDlpService.BuildDownloadArgs(
                url: "https://www.youtube.com/watch?v=abc123",
                outputTemplate: "/tmp/abc123.%(ext)s",
                format: YouTubeFormat.Audio,
                cookieFilePath: cookiePath,
                denoArgs: "",
                ffmpegDir: null);

            // Assert
            Assert.Contains($"--cookies \"{cookiePath}\"", args);
        }

        [Fact]
        public void WithDenoArgs_IncludesDenoArgs()
        {
            // Arrange
            var denoArgs = "--js-runtimes deno:\"/usr/bin/deno\" ";

            // Act
            var args = YtDlpService.BuildDownloadArgs(
                url: "https://www.youtube.com/watch?v=abc123",
                outputTemplate: "/tmp/abc123.%(ext)s",
                format: YouTubeFormat.Audio,
                cookieFilePath: null,
                denoArgs: denoArgs,
                ffmpegDir: null);

            // Assert
            Assert.StartsWith(denoArgs, args);
        }
    }

    public class UrlValidation
    {
        [Theory]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
        [InlineData("https://www.youtube.com/watch?v=abc123_-XYZ", "abc123_-XYZ")]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=120", "dQw4w9WgXcQ")]
        public void AcceptsValidVideoUrls(string url, string expectedVideoId)
        {
            // Arrange & Act
            var result = YtDlpService.ValidateUrl(url);

            // Assert
            Assert.Equal(expectedVideoId, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("https://www.google.com")]
        [InlineData("not a url")]
        [InlineData("https://vimeo.com/123456")]
        public void RejectsNonYouTubeUrls(string? url)
        {
            // Arrange & Act
            var result = YtDlpService.ValidateUrl(url!);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
        [InlineData("https://www.youtube.com/playlist?list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf")]
        [InlineData("https://www.youtube.com/watch?v=abc123_-XYZ&list=PL1234")]
        public void RejectsPlaylistUrls(string url)
        {
            // Arrange & Act
            var result = YtDlpService.ValidateUrl(url);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData("https://www.youtube.com/channel/UCuAXFkgsw1L7xaCfnd5JJOw")]
        [InlineData("https://www.youtube.com/@MrBeast")]
        [InlineData("https://www.youtube.com/c/MrBeast")]
        public void RejectsChannelUrls(string url)
        {
            // Arrange & Act
            var result = YtDlpService.ValidateUrl(url);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
        public void RejectsShortsUrls(string url)
        {
            // Arrange & Act
            var result = YtDlpService.ValidateUrl(url);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData("https://www.youtube.com/results?search_query=test")]
        public void RejectsSearchUrls(string url)
        {
            // Arrange & Act
            var result = YtDlpService.ValidateUrl(url);

            // Assert
            Assert.Null(result);
        }
    }

    public class MetadataParsing
    {
        [Fact]
        public void GetUploadDateTime_ParsesValidDate()
        {
            // Arrange
            var metadata = new YtDlpMetadata { UploadDate = "20231215" };

            // Act
            var result = metadata.GetUploadDateTime();

            // Assert
            Assert.NotNull(result);
            Assert.Equal(new DateTime(2023, 12, 15), result.Value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("2023")]
        [InlineData("not-a-date")]
        public void GetUploadDateTime_ReturnsNull_ForInvalidDate(string? uploadDate)
        {
            // Arrange
            var metadata = new YtDlpMetadata { UploadDate = uploadDate };

            // Act
            var result = metadata.GetUploadDateTime();

            // Assert
            Assert.Null(result);
        }
    }

    public class ExtractorErrorDetection
    {
        [Theory]
        [InlineData("ERROR: [youtube] abc123: ExtractorError: Unable to extract video data")]
        [InlineData("ERROR: unable to extract initial player response")]
        [InlineData("ERROR: unable to download webpage")]
        public void IsExtractorError_ReturnsTrue_ForKnownErrors(string stderr)
        {
            // Arrange & Act & Assert
            Assert.True(YtDlpService.IsExtractorError(stderr));
        }

        [Theory]
        [InlineData("ERROR: Video unavailable")]
        [InlineData("ERROR: Private video")]
        [InlineData("some other error")]
        public void IsExtractorError_ReturnsFalse_ForOtherErrors(string stderr)
        {
            // Arrange & Act & Assert
            Assert.False(YtDlpService.IsExtractorError(stderr));
        }
    }
}
