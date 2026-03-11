using FeatherPod.Server.Services;

namespace FeatherPod.Tests;

/// <summary>
/// Unit tests for FeedEventChannel — in-memory pub/sub for feed-level events (cross-tab sync).
/// </summary>
[Collection("Sequential")]
public class FeedEventChannelTests
{
    [Fact]
    public async Task Subscribe_ShouldReceivePublishedEvents()
    {
        // Arrange
        var channel = new FeedEventChannel();
        var reader = channel.Subscribe("feed1");

        // Act
        channel.Publish("feed1", "job-added");

        // Assert
        var received = await reader.ReadAsync();
        Assert.Equal("job-added", received);
    }

    [Fact]
    public void Publish_WithNoSubscribers_ShouldNotThrow()
    {
        // Arrange
        var channel = new FeedEventChannel();

        // Act & Assert
        var exception = Record.Exception(() => channel.Publish("feed1", "job-added"));
        Assert.Null(exception);
    }

    [Fact]
    public async Task MultipleSubscribers_ShouldAllReceiveEvents()
    {
        // Arrange
        var channel = new FeedEventChannel();
        var reader1 = channel.Subscribe("feed1");
        var reader2 = channel.Subscribe("feed1");

        // Act
        channel.Publish("feed1", "job-added");

        // Assert
        var received1 = await reader1.ReadAsync();
        var received2 = await reader2.ReadAsync();
        Assert.Equal("job-added", received1);
        Assert.Equal("job-added", received2);
    }

    [Fact]
    public async Task Subscribe_DifferentFeeds_ShouldBeIsolated()
    {
        // Arrange
        var channel = new FeedEventChannel();
        var reader1 = channel.Subscribe("feed1");
        var reader2 = channel.Subscribe("feed2");

        // Act
        channel.Publish("feed1", "job-added");

        // Assert
        var received = await reader1.ReadAsync();
        Assert.Equal("job-added", received);
        Assert.False(reader2.TryRead(out _));
    }

    [Fact]
    public void Unsubscribe_ShouldCompleteChannel()
    {
        // Arrange
        var channel = new FeedEventChannel();
        var reader = channel.Subscribe("feed1");

        // Act
        channel.Unsubscribe("feed1", reader);

        // Assert
        Assert.True(reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task Unsubscribe_ShouldNotAffectOtherSubscribers()
    {
        // Arrange
        var channel = new FeedEventChannel();
        var reader1 = channel.Subscribe("feed1");
        var reader2 = channel.Subscribe("feed1");

        // Act
        channel.Unsubscribe("feed1", reader1);
        channel.Publish("feed1", "job-added");

        // Assert
        Assert.True(reader1.Completion.IsCompleted);
        var received = await reader2.ReadAsync();
        Assert.Equal("job-added", received);
    }

    [Fact]
    public void Unsubscribe_NonExistentFeed_ShouldNotThrow()
    {
        // Arrange
        var channel = new FeedEventChannel();

        // Act & Assert
        var exception = Record.Exception(() => channel.Unsubscribe("nonexistent", null!));
        Assert.Null(exception);
    }

    [Fact]
    public void BoundedChannel_ShouldDropOldestWhenFull()
    {
        // Arrange — channel capacity is 10
        var channel = new FeedEventChannel();
        var reader = channel.Subscribe("feed1");

        // Act — publish 15 events (exceeds capacity of 10)
        for (var i = 0; i < 15; i++)
        {
            channel.Publish("feed1", $"event-{i}");
        }

        // Assert — should have dropped oldest, keeping last 10
        var received = new List<string>();
        while (reader.TryRead(out var item))
        {
            received.Add(item);
        }

        Assert.Equal(10, received.Count);
        Assert.Equal("event-14", received.Last());
    }

    [Fact]
    public void ConcurrentPublish_ShouldNotThrow()
    {
        // Arrange
        var channel = new FeedEventChannel();
        var reader = channel.Subscribe("feed1");

        // Act — publish from multiple threads concurrently
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => channel.Publish("feed1", $"event-{i}"))
        ).ToArray();

        // Assert
        var exception = Record.Exception(() => Task.WaitAll(tasks));
        Assert.Null(exception);

        // Clean up
        channel.Unsubscribe("feed1", reader);
    }

    [Fact]
    public void ConcurrentSubscribeUnsubscribe_ShouldNotThrow()
    {
        // Arrange
        var channel = new FeedEventChannel();

        // Act — subscribe and unsubscribe from multiple threads
        var tasks = Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() =>
            {
                var reader = channel.Subscribe("feed1");
                channel.Publish("feed1", "job-added");
                channel.Unsubscribe("feed1", reader);
            })
        ).ToArray();

        // Assert
        var exception = Record.Exception(() => Task.WaitAll(tasks));
        Assert.Null(exception);
    }
}
