using Microsoft.Extensions.Hosting;

namespace FeatherPod.Tests.Helpers;

/// <summary>
/// Minimal <see cref="IHostApplicationLifetime"/> stub for tests that host a
/// <see cref="BackgroundService"/>. <see cref="StopApplication"/> cancels the stopping/stopped
/// tokens; <see cref="ApplicationStarted"/> is pre-canceled in the constructor so background
/// services that wait on it can proceed immediately.
/// </summary>
public sealed class StubLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();
    private bool _disposed;

    public StubLifetime()
    {
        _started.Cancel();
    }

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public void StopApplication()
    {
        _stopping.Cancel();
        _stopped.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}
