using System.Threading.Channels;
using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Unbounded channel for transcription requests.
/// </summary>
public class TranscriptionChannel : ITranscriptionChannel
{
    private readonly Channel<TranscriptionRequest> _channel = Channel.CreateUnbounded<TranscriptionRequest>(
        new UnboundedChannelOptions { SingleReader = false });

    public ValueTask SubmitAsync(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<TranscriptionRequest> ReadAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }
}
