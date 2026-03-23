using FeatherPod.Server.Services;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class TempFileCleanupServiceTests : IDisposable
{
    private readonly string _testDirectory;

    public TempFileCleanupServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public void CleanupStaleEntries_DeletesStaleDirectories()
    {
        // Arrange
        var staleDir = Path.Combine(_testDirectory, "stale-dir");
        var freshDir = Path.Combine(_testDirectory, "fresh-dir");
        Directory.CreateDirectory(staleDir);
        Directory.CreateDirectory(freshDir);
        Directory.SetLastWriteTimeUtc(staleDir, DateTime.UtcNow.AddHours(-2));

        // Act
        var result = TempFileCleanupService.CleanupStaleEntries(_testDirectory, TimeSpan.FromHours(1));

        // Assert
        Assert.False(Directory.Exists(staleDir));
        Assert.True(Directory.Exists(freshDir));
        Assert.Equal(1, result.DeletedDirs);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.Errors);
    }

    [Fact]
    public void CleanupStaleEntries_DeletesStaleFiles()
    {
        // Arrange
        var staleFile = Path.Combine(_testDirectory, "stale.mp3");
        var freshFile = Path.Combine(_testDirectory, "fresh.mp3");
        File.WriteAllText(staleFile, "old audio");
        File.WriteAllText(freshFile, "new audio");
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddHours(-2));

        // Act
        var result = TempFileCleanupService.CleanupStaleEntries(_testDirectory, TimeSpan.FromHours(1));

        // Assert
        Assert.False(File.Exists(staleFile));
        Assert.True(File.Exists(freshFile));
        Assert.Equal(0, result.DeletedDirs);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(0, result.Errors);
    }

    [Fact]
    public void CleanupStaleEntries_NonExistentDirectory_ReturnsZeros()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDirectory, "does-not-exist");

        // Act
        var result = TempFileCleanupService.CleanupStaleEntries(nonExistentPath, TimeSpan.FromHours(1));

        // Assert
        Assert.Equal(0, result.DeletedDirs);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(0, result.Errors);
    }

    [Fact]
    public void CleanupStaleEntries_ContinuesAfterErrors()
    {
        // Arrange
        var lockedFile = Path.Combine(_testDirectory, "locked.mp3");
        var staleFile = Path.Combine(_testDirectory, "stale.mp3");
        File.WriteAllText(lockedFile, "locked audio");
        File.WriteAllText(staleFile, "stale audio");
        File.SetLastWriteTimeUtc(lockedFile, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddHours(-2));

        // Act - hold a file handle open to prevent deletion
        using (var stream = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = TempFileCleanupService.CleanupStaleEntries(_testDirectory, TimeSpan.FromHours(1));

            // Assert
            Assert.True(File.Exists(lockedFile));
            Assert.False(File.Exists(staleFile));
            Assert.Equal(1, result.DeletedFiles);
            Assert.Equal(1, result.Errors);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
