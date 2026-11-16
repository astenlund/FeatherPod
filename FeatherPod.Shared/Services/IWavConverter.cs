namespace FeatherPod.Shared.Services;

/// <summary>
/// Converts audio to 16 kHz mono WAV, the input format the Azure Speech endpoints accept for
/// containers they reject (M4A/M4B/MP4).
/// </summary>
public interface IWavConverter
{
    /// <summary>
    /// Writes <paramref name="inputPath"/> as 16 kHz mono WAV to <paramref name="wavPath"/>,
    /// overwriting it. On failure or cancellation the output is deleted before the exception
    /// propagates, so callers can treat a thrown conversion as leaving no output behind.
    /// </summary>
    Task ConvertAsync(string inputPath, string wavPath, CancellationToken ct);
}
