using FeatherPod.Shared.Services;

namespace FeatherPod.Tests;

public sealed class FFmpegBinaryManagerTests : IDisposable
{
    private readonly string _binDir = Path.Combine(Path.GetTempPath(), "FeatherPod.Tests", $"ffmpeg-bin-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_binDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // The missing-directory test never creates it.
        }
    }

    [Fact]
    public void GetLocalFFmpegDirectory_BothBinariesPresent_ReturnsDirectory()
    {
        // Arrange
        CreateBinaries("ffmpeg", "ffprobe");

        // Act
        var result = FFmpegBinaryManager.GetLocalFFmpegDirectory(_binDir);

        // Assert
        Assert.Equal(_binDir, result);
    }

    [Theory]
    [InlineData("ffmpeg")]
    [InlineData("ffprobe")]
    public void GetLocalFFmpegDirectory_OnlyOneBinaryPresent_ReturnsNull(string presentTool)
    {
        // Arrange
        CreateBinaries(presentTool);

        // Act
        var result = FFmpegBinaryManager.GetLocalFFmpegDirectory(_binDir);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetLocalFFmpegDirectory_DirectoryMissing_ReturnsNull()
    {
        // Act
        var result = FFmpegBinaryManager.GetLocalFFmpegDirectory(_binDir);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("ffmpeg")]
    [InlineData("ffprobe")]
    public void ResolveExecutablePath_BothBinariesPresent_ReturnsLocalPath(string tool)
    {
        // Arrange
        CreateBinaries("ffmpeg", "ffprobe");

        // Act
        var result = FFmpegBinaryManager.ResolveExecutablePath(_binDir, tool);

        // Assert
        Assert.Equal(Path.Combine(_binDir, ExecutableName(tool)), result);
    }

    [Theory]
    [InlineData("ffmpeg")]
    [InlineData("ffprobe")]
    public void ResolveExecutablePath_PartialLocalDownload_FallsBackToPathForBothTools(string tool)
    {
        // Arrange
        CreateBinaries("ffmpeg");

        // Act
        var result = FFmpegBinaryManager.ResolveExecutablePath(_binDir, tool);

        // Assert
        Assert.Equal(tool, result);
    }

    private static string ExecutableName(string tool)
    {
        return OperatingSystem.IsWindows() ? $"{tool}.exe" : tool;
    }

    private void CreateBinaries(params string[] tools)
    {
        Directory.CreateDirectory(_binDir);
        foreach (var tool in tools)
        {
            File.WriteAllBytes(Path.Combine(_binDir, ExecutableName(tool)), []);
        }
    }
}
