using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class JobStatusEntityTests
{
    [Fact]
    public void CreateQueued_WithModeAndInterval_ShouldStoreValues()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued(new CreateJobOptions { JobId = "job-123", FeedId = "feed-1", FileName = "test.mp3", ProgressMode = "push", ProgressIntervalMs = 250 });

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
        var entity = JobStatusEntity.CreateQueued(new CreateJobOptions { JobId = "job-456", FeedId = "feed-2", FileName = "audio.mp3" });

        // Assert
        Assert.Null(entity.ProgressMode);
        Assert.Null(entity.ProgressIntervalMs);
        Assert.Null(entity.Title);
    }

    [Fact]
    public void CreateQueued_WithSignalrMode_ShouldStoreSignalr()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued(new CreateJobOptions { JobId = "job-789", FeedId = "feed-3", ProgressMode = "signalr", ProgressIntervalMs = 100 });

        // Assert
        Assert.Equal("signalr", entity.ProgressMode);
        Assert.Equal(100, entity.ProgressIntervalMs);
    }

    [Fact]
    public void CreateQueued_WithTitle_ShouldStoreTitle()
    {
        // Arrange & Act
        var entity = JobStatusEntity.CreateQueued(new CreateJobOptions { JobId = "job-101", FeedId = "feed-4", FileName = "episode.mp3", Title = "My Great Episode" });

        // Assert
        Assert.Equal("My Great Episode", entity.Title);
        Assert.Equal("episode.mp3", entity.FileName);
        Assert.Null(entity.ProgressMode);
    }
}
