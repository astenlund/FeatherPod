using FeatherPod.Shared;

namespace FeatherPod.Tests;

public class SharedRetryableOperationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunAsync_ConcurrentCallers_ShareOneRun()
    {
        // Arrange
        var calls = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new SharedRetryableOperation(() =>
        {
            Interlocked.Increment(ref calls);

            return release.Task;
        });

        // Act
        var runs = Enumerable.Range(0, 8).Select(_ => operation.RunAsync(CancellationToken.None)).ToList();
        release.SetResult(true);
        var results = await Task.WhenAll(runs).WaitAsync(Timeout);

        // Assert
        Assert.All(results, Assert.True);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_AfterSuccess_ReusesResultWithoutRerunning()
    {
        // Arrange
        var calls = 0;
        var operation = new SharedRetryableOperation(() =>
        {
            Interlocked.Increment(ref calls);

            return Task.FromResult(true);
        });
        await operation.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        // Act
        var result = await operation.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        // Assert
        Assert.True(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_AfterFalse_StartsNewRun()
    {
        // Arrange
        var calls = 0;
        var operation = new SharedRetryableOperation(() => Task.FromResult(Interlocked.Increment(ref calls) > 1));
        var first = await operation.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        // Act
        var second = await operation.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        // Assert
        Assert.False(first);
        Assert.True(second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunAsync_AfterException_StartsNewRun()
    {
        // Arrange
        var calls = 0;
        var operation = new SharedRetryableOperation(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("first attempt fails");
            }

            return Task.FromResult(true);
        });
        await Assert.ThrowsAsync<IOException>(() => operation.RunAsync(CancellationToken.None).WaitAsync(Timeout));

        // Act
        var result = await operation.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        // Assert
        Assert.True(result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RunAsync_CancelledCaller_DoesNotCancelSharedRun()
    {
        // Arrange
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new SharedRetryableOperation(() => release.Task);
        using var cts = new CancellationTokenSource();
        var cancelledRun = operation.RunAsync(cts.Token);
        var otherRun = operation.RunAsync(CancellationToken.None);

        // Act
        await cts.CancelAsync();
        release.SetResult(true);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRun.WaitAsync(Timeout));
        Assert.True(await otherRun.WaitAsync(Timeout));
    }
}
