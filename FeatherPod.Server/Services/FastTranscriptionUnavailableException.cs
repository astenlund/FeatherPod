namespace FeatherPod.Server.Services;

/// <summary>
/// Thrown when the Azure Speech Fast Transcription endpoint cannot service a particular
/// request (typically because the audio exceeds the duration/size cap for diarized Fast,
/// or because Fast is not available in the current region). Callers should fall back to
/// the batch transcription path.
/// </summary>
public sealed class FastTranscriptionUnavailableException : Exception
{
    public FastTranscriptionUnavailableException(string message) : base(message) { }

    public FastTranscriptionUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
