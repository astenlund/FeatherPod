using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class FileTrashServiceTests : IDisposable
{
    private readonly string _testDirectory;

    public FileTrashServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void TryDeleteFile_PermanentDelete_RemovesFile()
    {
        var filePath = Path.Combine(_testDirectory, "test.mp3");
        File.WriteAllText(filePath, "audio data");
        Assert.True(File.Exists(filePath));

        var result = FileTrashService.TryDeleteFile(filePath, useTrash: false);

        Assert.True(result.Success);
        Assert.False(File.Exists(filePath));
        Assert.Equal("permanently deleted", result.Method);
    }

    [Fact]
    public void TryDeleteFile_NonExistentFile_ReturnsFalse()
    {
        var filePath = Path.Combine(_testDirectory, "nonexistent.mp3");

        var result = FileTrashService.TryDeleteFile(filePath, useTrash: false);

        Assert.False(result.Success);
    }

    [Fact]
    public void TryDeleteFile_WithTrash_DeletesFile()
    {
        var filePath = Path.Combine(_testDirectory, "test.mp3");
        File.WriteAllText(filePath, "audio data");
        Assert.True(File.Exists(filePath));

        var result = FileTrashService.TryDeleteFile(filePath, useTrash: true);

        Assert.True(result.Success);
        Assert.False(File.Exists(filePath));
        // Method will be "sent to trash", "permanently deleted", or "permanently deleted (trash unavailable)" depending on platform
        Assert.NotNull(result.Method);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try { Directory.Delete(_testDirectory, recursive: true); }
            catch { /* ignore cleanup errors */ }
        }
    }
}
