using FeatherPod.Shared.Models;

namespace FeatherPod.Shared.Services;

public interface ITranscriptionService
{
    bool IsAvailable { get; }

    Task<string?> TranscribeAsync(string audioPath, Action<ProgressUpdate>? progressCallback = null, CancellationToken cancellationToken = default);
}
