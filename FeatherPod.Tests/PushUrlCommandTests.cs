using FeatherPod.Commands.Feed;
using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class PushUrlCommandTests : IDisposable
{
    private readonly string _tempDir;

    public PushUrlCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"featherpod-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        PreferencesHelpers.PreferencesDirectoryOverride = _tempDir;
    }

    public void Dispose()
    {
        PreferencesHelpers.PreferencesDirectoryOverride = null;

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task GetPushUrlAsync_WithApiKey_ReturnsSuccess()
    {
        // Arrange - save an API key for Dev environment
        PreferencesHelpers.SaveApiKey("Dev", "fp_testuser_secretkey123");

        // Act
        var result = await PushUrlCommand.GetPushUrlAsync("Dev", "my-podcast");

        // Assert
        Assert.True(result.Success);
        Assert.Equal("my-podcast", result.FeedId);
    }

    [Fact]
    public async Task GetPushUrlAsync_WithoutApiKey_ReturnsFailure()
    {
        // Arrange - no API key saved (temp dir is empty)

        // Act
        var result = await PushUrlCommand.GetPushUrlAsync("Dev", "my-podcast");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("No API key", result.ErrorMessage);
    }

    [Fact]
    public async Task GetPushUrlAsync_CopyToClipboard_StillReturnsSuccess()
    {
        // Arrange
        PreferencesHelpers.SaveApiKey("Dev", "fp_testuser_secretkey123");

        // Act - clipboard may not be available in CI/test environment, but should not fail
        var result = await PushUrlCommand.GetPushUrlAsync("Dev", "my-podcast", copyToClipboard: true);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("my-podcast", result.FeedId);
    }
}
