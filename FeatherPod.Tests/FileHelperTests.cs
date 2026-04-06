using FeatherPod.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class FileHelperTests
{
    [Fact]
    public void TryDeleteFile_NullPath_NoOp()
    {
        // Arrange & Act & Assert (should not throw)
        FileHelper.TryDeleteFile(null, NullLogger.Instance);
    }

    [Fact]
    public void TryDeleteFile_EmptyPath_NoOp()
    {
        // Arrange & Act & Assert (should not throw)
        FileHelper.TryDeleteFile(string.Empty, NullLogger.Instance);
    }

    [Fact]
    public void TryDeleteFile_NonExistentPath_NoOp()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");

        // Act & Assert (should not throw)
        FileHelper.TryDeleteFile(path, NullLogger.Instance);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryDeleteFile_ExistingFile_DeletesIt()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"delete-me-{Guid.NewGuid():N}");
        File.WriteAllText(path, "content");
        Assert.True(File.Exists(path));

        // Act
        FileHelper.TryDeleteFile(path, NullLogger.Instance);

        // Assert
        Assert.False(File.Exists(path));
    }
}
