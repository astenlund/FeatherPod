using FeatherPod.Server.Services;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

/// <summary>
/// Unit tests for JobProgressChannel — in-memory pub/sub for job progress updates.
/// </summary>
[Collection("Sequential")]
public class JobProgressChannelTests
{
    private static JobStatusResponse CreateResponse(string jobId, string status = "Processing", int percent = 50, string? stage = "Normalizing")
    {
        return new JobStatusResponse
        {
            JobId = jobId,
            Status = status,
            Stage = stage,
            ProgressPercent = percent,
            ProgressMessage = $"Progress {percent}%"
        };
    }

    [Fact]
    public async Task Subscribe_ShouldReceivePublishedUpdates()
    {
        // Arrange
        var channel = new JobProgressChannel();
        var reader = channel.Subscribe("job1");
        var update = CreateResponse("job1");

        // Act
        channel.Publish("job1", update);

        // Assert
        var received = await reader.ReadAsync();
        Assert.Equal("job1", received.JobId);
        Assert.Equal(50, received.ProgressPercent);
    }

    [Fact]
    public void Publish_WithNoSubscribers_ShouldNotThrow()
    {
        // Arrange
        var channel = new JobProgressChannel();

        // Act & Assert
        var exception = Record.Exception(() => channel.Publish("job1", CreateResponse("job1")));
        Assert.Null(exception);
    }

    [Fact]
    public async Task MultipleSubscribers_ShouldAllReceiveUpdates()
    {
        // Arrange
        var channel = new JobProgressChannel();
        var reader1 = channel.Subscribe("job1");
        var reader2 = channel.Subscribe("job1");
        var update = CreateResponse("job1", percent: 75);

        // Act
        channel.Publish("job1", update);

        // Assert
        var received1 = await reader1.ReadAsync();
        var received2 = await reader2.ReadAsync();
        Assert.Equal(75, received1.ProgressPercent);
        Assert.Equal(75, received2.ProgressPercent);
    }

    [Fact]
    public async Task Subscribe_DifferentJobs_ShouldBeIsolated()
    {
        // Arrange
        var channel = new JobProgressChannel();
        var reader1 = channel.Subscribe("job1");
        var reader2 = channel.Subscribe("job2");

        // Act
        channel.Publish("job1", CreateResponse("job1", percent: 10));
        channel.Publish("job2", CreateResponse("job2", percent: 20));

        // Assert
        var received1 = await reader1.ReadAsync();
        var received2 = await reader2.ReadAsync();
        Assert.Equal(10, received1.ProgressPercent);
        Assert.Equal(20, received2.ProgressPercent);
    }

    [Fact]
    public void Unsubscribe_ShouldCompleteChannel()
    {
        // Arrange
        var channel = new JobProgressChannel();
        var reader = channel.Subscribe("job1");

        // Act
        channel.Unsubscribe("job1", reader);

        // Assert
        Assert.True(reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task Unsubscribe_ShouldNotAffectOtherSubscribers()
    {
        // Arrange
        var channel = new JobProgressChannel();
        var reader1 = channel.Subscribe("job1");
        var reader2 = channel.Subscribe("job1");

        // Act
        channel.Unsubscribe("job1", reader1);
        channel.Publish("job1", CreateResponse("job1", percent: 99));

        // Assert
        Assert.True(reader1.Completion.IsCompleted);
        var received = await reader2.ReadAsync();
        Assert.Equal(99, received.ProgressPercent);
    }

    [Fact]
    public void Unsubscribe_NonExistentJob_ShouldNotThrow()
    {
        // Arrange
        var channel = new JobProgressChannel();

        // Act & Assert
        var exception = Record.Exception(() => channel.Unsubscribe("nonexistent", null!));
        Assert.Null(exception);
    }

    [Fact]
    public void BoundedChannel_ShouldDropOldestWhenFull()
    {
        // Arrange — channel capacity is 10
        var channel = new JobProgressChannel();
        var reader = channel.Subscribe("job1");

        // Act — publish 15 updates (exceeds capacity of 10)
        for (var i = 0; i < 15; i++)
        {
            channel.Publish("job1", CreateResponse("job1", percent: i));
        }

        // Assert — should have dropped oldest, keeping last 10
        var received = new List<JobStatusResponse>();
        while (reader.TryRead(out var item))
        {
            received.Add(item);
        }

        Assert.Equal(10, received.Count);
        // Last item should be percent=14 (the most recent)
        Assert.Equal(14, received.Last().ProgressPercent);
    }

    [Fact]
    public void ConcurrentPublish_ShouldNotThrow()
    {
        // Arrange
        var channel = new JobProgressChannel();
        var reader = channel.Subscribe("job1");

        // Act — publish from multiple threads concurrently
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => channel.Publish("job1", CreateResponse("job1", percent: i)))
        ).ToArray();

        // Assert
        var exception = Record.Exception(() => Task.WaitAll(tasks));
        Assert.Null(exception);

        // Clean up
        channel.Unsubscribe("job1", reader);
    }

    [Fact]
    public void ConcurrentSubscribeUnsubscribe_ShouldNotThrow()
    {
        // Arrange
        var channel = new JobProgressChannel();

        // Act — subscribe and unsubscribe from multiple threads
        var tasks = Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() =>
            {
                var reader = channel.Subscribe("job1");
                channel.Publish("job1", CreateResponse("job1"));
                channel.Unsubscribe("job1", reader);
            })
        ).ToArray();

        // Assert
        var exception = Record.Exception(() => Task.WaitAll(tasks));
        Assert.Null(exception);
    }
}
