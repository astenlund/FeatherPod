using FeatherPod.Shared.Models;

namespace FeatherPod.Server.Services;

/// <summary>
/// Channel for submitting transcription requests. Fire-and-forget from the controller,
/// consumed by TranscriptionBackgroundService.
/// </summary>
public interface ITranscriptionChannel
{
    ValueTask SubmitAsync(TranscriptionRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TranscriptionRequest> ReadAllAsync(CancellationToken cancellationToken);
}
