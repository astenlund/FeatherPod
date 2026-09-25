namespace FeatherPod.Shared;

/// <summary>
/// Runs an asynchronous operation at most once at a time: callers that arrive while a run is in
/// flight share its task. A successful run is kept, so later callers get its result immediately.
/// A run that returns false or throws is forgotten, so the next call starts a fresh attempt.
/// </summary>
internal sealed class SharedRetryableOperation
{
    private readonly Func<Task<bool>> _operation;
    private readonly Lock _lock = new();
    private Task<bool>? _current;

    public SharedRetryableOperation(Func<Task<bool>> operation)
    {
        _operation = operation;
    }

    /// <summary>
    /// Joins the in-flight run, or starts one when none is in flight.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait only; the shared run continues for the others.</param>
    public Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        Task<bool> run;
        lock (_lock)
        {
            // Task.Run keeps the operation (including its failure reset) off this thread, so the
            // reset can never run before the task is published to _current.
            run = _current ??= Task.Run(RunAndForgetFailureAsync);
        }

        return run.WaitAsync(cancellationToken);
    }

    private async Task<bool> RunAndForgetFailureAsync()
    {
        var succeeded = false;
        try
        {
            succeeded = await _operation();

            return succeeded;
        }
        finally
        {
            if (!succeeded)
            {
                lock (_lock)
                {
                    _current = null;
                }
            }
        }
    }
}
