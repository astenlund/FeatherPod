using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class JobStatusEntityTests
{
    [Fact]
    public void CreateQueued_WithModeAndInterval_ShouldStoreValues()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued("job-123", "feed-1", "test.mp3", progressMode: "push", progressIntervalMs: 250);

        // Assert
        Assert.Equal("push", entity.ProgressMode);
        Assert.Equal(250, entity.ProgressIntervalMs);
        Assert.Equal("job-123", entity.RowKey);
        Assert.Equal("feed-1", entity.FeedId);
        Assert.Equal("test.mp3", entity.FileName);
        Assert.Null(entity.Title);
    }

    [Fact]
    public void CreateQueued_WithoutModeAndInterval_ShouldDefaultToNull()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued("job-456", "feed-2", "audio.mp3");

        // Assert
        Assert.Null(entity.ProgressMode);
        Assert.Null(entity.ProgressIntervalMs);
        Assert.Null(entity.Title);
    }

    [Fact]
    public void CreateQueued_WithSignalrMode_ShouldStoreSignalr()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued("job-789", "feed-3", progressMode: "signalr", progressIntervalMs: 100);

        // Assert
        Assert.Equal("signalr", entity.ProgressMode);
        Assert.Equal(100, entity.ProgressIntervalMs);
    }

    [Fact]
    public void CreateQueued_WithTitle_ShouldStoreTitle()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued("job-101", "feed-4", "episode.mp3", title: "My Great Episode");

        // Assert
        Assert.Equal("My Great Episode", entity.Title);
        Assert.Equal("episode.mp3", entity.FileName);
        Assert.Null(entity.ProgressMode);
    }
}
