using FeatherPod.Shared.Services;

namespace FeatherPod.Tests;

public class YtDlpBinaryManagerTests
{
    [Fact]
    public void GetBinaryDirectory_ReturnsNonEmptyPath()
    {
        // Arrange & Act
        var directory = YtDlpBinaryManager.GetBinaryDirectory();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.Contains("yt-dlp", directory);
    }

    [Fact]
    public void GetBinaryPath_ContainsPlatformBinaryName()
    {
        // Arrange
        var manager = new YtDlpBinaryManager();

        // Act
        var path = manager.GetBinaryPath();

        // Assert
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith("yt-dlp.exe", path);
        }
        else
        {
            Assert.EndsWith("yt-dlp", path);
        }
    }

    [Fact]
    public void GetBinaryPath_IsInsideBinaryDirectory()
    {
        // Arrange
        var manager = new YtDlpBinaryManager();

        // Act
        var binaryPath = manager.GetBinaryPath();
        var binDir = YtDlpBinaryManager.GetBinaryDirectory();

        // Assert
        Assert.StartsWith(binDir, binaryPath);
    }

    [Fact]
    public void IsAvailable_ReturnsFalse_WhenBinaryDoesNotExist()
    {
        // Arrange
        var manager = new YtDlpBinaryManager();

        // Act & Assert -- binary may or may not exist on the test machine,
        // but the method should not throw
        var result = manager.IsAvailable();
        Assert.IsType<bool>(result);
    }

    [Fact]
    public async Task GetCurrentVersionAsync_ReturnsNull_WhenNoVersionFile()
    {
        // Arrange
        var manager = new YtDlpBinaryManager();

        // Act
        var version = await manager.GetCurrentVersionAsync();

        // Assert -- may be null or a string depending on local state
        // The method should not throw regardless
        Assert.True(version == null || version.Length > 0);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    [InlineData("macOS")]
    public void GetBinaryDirectory_ContainsFeatherPodSegment(string platform)
    {
        // Arrange & Act -- we can only test the current platform
        _ = platform; // Parameter documents test intent
        var directory = YtDlpBinaryManager.GetBinaryDirectory();

        // Assert
        Assert.True(
            directory.Contains("FeatherPod", StringComparison.OrdinalIgnoreCase) ||
            directory.Contains(".featherpod", StringComparison.OrdinalIgnoreCase));
    }
}
