using System.Runtime.Versioning;
using System.Text.Json;
using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

[Collection("Sequential")]
[SupportedOSPlatform("windows")]
public class SingleInstanceCoordinatorTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _tempDir;

    public SingleInstanceCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SingleInstanceCoordinatorTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        PreferencesHelpers.PreferencesDirectoryOverride = _tempDir;
    }

    [Fact]
    public void TryBecomeHost_ReturnsTrue_WhenNoLockFile()
    {
        // Arrange
        var feedId = $"test-{Guid.NewGuid():N}";
        using var coordinator = new SingleInstanceCoordinator(feedId);

        // Act
        var isHost = coordinator.TryBecomeHost(out var existingHost);

        // Assert
        Assert.True(isHost);
        Assert.Null(existingHost);
    }

    [Fact]
    public void TryBecomeHost_ReturnsFalse_WhenExistingHostIsValid()
    {
        // Arrange
        var feedId = $"test-{Guid.NewGuid():N}";
        using var server = new LocalFileServer("http://localhost");
        server.Start();

        using var coordinator1 = new SingleInstanceCoordinator(feedId);
        Assert.True(coordinator1.TryBecomeHost(out _));
        coordinator1.WriteLockFile(server.Port, server.Token);

        using var coordinator2 = new SingleInstanceCoordinator(feedId);

        // Act
        var isHost = coordinator2.TryBecomeHost(out var existingHost);

        // Assert
        Assert.False(isHost);
        Assert.NotNull(existingHost);
        Assert.Equal(server.Port, existingHost.Port);
        Assert.Equal(server.Token, existingHost.Token);
        Assert.Equal(feedId, existingHost.FeedId);
    }

    [Fact]
    public void TryBecomeHost_ReturnsTrue_WhenLockFileHasStalePid()
    {
        // Arrange
        var feedId = $"test-{Guid.NewGuid():N}";
        var lockFilePath = Path.Combine(_tempDir, $"context-menu-server-{feedId}.json");

        var staleInfo = new LockFileInfo
        {
            Port = 12345,
            Token = "stale-token",
            FeedId = feedId,
            Pid = int.MaxValue - 1,
        };
        File.WriteAllText(lockFilePath, JsonSerializer.Serialize(staleInfo, JsonOptions));

        using var coordinator = new SingleInstanceCoordinator(feedId);

        // Act
        var isHost = coordinator.TryBecomeHost(out var existingHost);

        // Assert
        Assert.True(isHost);
        Assert.Null(existingHost);
    }

    [Fact]
    public void TryBecomeHost_ReturnsTrue_WhenPidValidButServerNotResponding()
    {
        // Arrange
        var feedId = $"test-{Guid.NewGuid():N}";
        var lockFilePath = Path.Combine(_tempDir, $"context-menu-server-{feedId}.json");

        var info = new LockFileInfo
        {
            Port = 1,
            Token = "some-token",
            FeedId = feedId,
            Pid = Environment.ProcessId,
        };
        File.WriteAllText(lockFilePath, JsonSerializer.Serialize(info, JsonOptions));

        using var coordinator = new SingleInstanceCoordinator(feedId);

        // Act
        var isHost = coordinator.TryBecomeHost(out var existingHost);

        // Assert
        Assert.True(isHost);
        Assert.Null(existingHost);
    }

    [Fact]
    public void WriteLockFile_CreatesValidJson()
    {
        // Arrange
        var feedId = $"test-{Guid.NewGuid():N}";
        using var coordinator = new SingleInstanceCoordinator(feedId);
        coordinator.TryBecomeHost(out _);

        // Act
        coordinator.WriteLockFile(8080, "test-token");

        // Assert
        var lockFilePath = Path.Combine(_tempDir, $"context-menu-server-{feedId}.json");
        Assert.True(File.Exists(lockFilePath));

        var json = File.ReadAllText(lockFilePath);
        var info = JsonSerializer.Deserialize<LockFileInfo>(json, JsonOptions);
        Assert.NotNull(info);
        Assert.Equal(8080, info.Port);
        Assert.Equal("test-token", info.Token);
        Assert.Equal(feedId, info.FeedId);
        Assert.Equal(Environment.ProcessId, info.Pid);
    }

    [Fact]
    public void DeleteLockFile_RemovesFile()
    {
        // Arrange
        var feedId = $"test-{Guid.NewGuid():N}";
        using var coordinator = new SingleInstanceCoordinator(feedId);
        coordinator.TryBecomeHost(out _);
        coordinator.WriteLockFile(8080, "test-token");
        var lockFilePath = Path.Combine(_tempDir, $"context-menu-server-{feedId}.json");
        Assert.True(File.Exists(lockFilePath));

        // Act
        coordinator.DeleteLockFile();

        // Assert
        Assert.False(File.Exists(lockFilePath));
    }

    [Fact]
    public void DifferentFeedIds_UseSeparateMutexes()
    {
        // Arrange
        var feedId1 = $"test-{Guid.NewGuid():N}";
        var feedId2 = $"test-{Guid.NewGuid():N}";
        using var coordinator1 = new SingleInstanceCoordinator(feedId1);
        using var coordinator2 = new SingleInstanceCoordinator(feedId2);

        // Act
        var isHost1 = coordinator1.TryBecomeHost(out _);
        var isHost2 = coordinator2.TryBecomeHost(out _);

        // Assert
        Assert.True(isHost1);
        Assert.True(isHost2);
    }

    [Fact]
    public void WriteLockFile_CreatesDirectoryIfMissing()
    {
        // Arrange
        var nestedDir = Path.Combine(_tempDir, "nested", "subdir");
        PreferencesHelpers.PreferencesDirectoryOverride = nestedDir;

        var feedId = $"test-{Guid.NewGuid():N}";
        using var coordinator = new SingleInstanceCoordinator(feedId);
        coordinator.TryBecomeHost(out _);

        // Act
        coordinator.WriteLockFile(9090, "nested-token");

        // Assert
        var lockFilePath = Path.Combine(nestedDir, $"context-menu-server-{feedId}.json");
        Assert.True(File.Exists(lockFilePath));
    }

    [Fact]
    public void Dispose_ReleasesMutex_NextCallerBecomesHost()
    {
        // Arrange
        var feedId = $"test-{Guid.NewGuid():N}";
        using var server = new LocalFileServer("http://localhost");
        server.Start();

        var coordinator1 = new SingleInstanceCoordinator(feedId);
        Assert.True(coordinator1.TryBecomeHost(out _));
        coordinator1.WriteLockFile(server.Port, server.Token);

        server.Dispose();
        coordinator1.Dispose();

        using var coordinator2 = new SingleInstanceCoordinator(feedId);

        // Act
        var isHost = coordinator2.TryBecomeHost(out var existingHost);

        // Assert
        Assert.True(isHost);
        Assert.Null(existingHost);
    }

    [Fact]
    public void TryBecomeHost_ReturnsTrue_WhenLockFileIsCorrupt()
    {
        // Arrange
        var feedId = $"test-{Guid.NewGuid():N}";
        var lockFilePath = Path.Combine(_tempDir, $"context-menu-server-{feedId}.json");
        File.WriteAllText(lockFilePath, "{{not valid json!!");

        using var coordinator = new SingleInstanceCoordinator(feedId);

        // Act
        var isHost = coordinator.TryBecomeHost(out var existingHost);

        // Assert
        Assert.True(isHost);
        Assert.Null(existingHost);
    }

    public void Dispose()
    {
        PreferencesHelpers.PreferencesDirectoryOverride = null;

        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
