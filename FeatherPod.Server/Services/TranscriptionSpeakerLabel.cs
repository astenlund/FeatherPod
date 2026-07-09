using System.Text.Json;

namespace FeatherPod.Server.Services;

/// <summary>
/// Shared speaker-label extraction for <see cref="FastTranscriptionParser"/> and
/// <see cref="BatchTranscriptionParser"/>.
/// </summary>
internal static class TranscriptionSpeakerLabel
{
    /// <summary>
    /// Reads the phrase's <c>speaker</c> field as a "Speaker N" label. The field is a JSON
    /// number (e.g. <c>"speaker": 0</c>); <see cref="object.ToString"/> handles both numeric
    /// and string tokens, whereas <c>GetString()</c> throws on numeric tokens. A missing
    /// field (diarization disabled) maps to "Speaker 0".
    /// </summary>
    public static string FromPhrase(JsonElement phrase)
        => $"Speaker {(phrase.TryGetProperty("speaker", out var sp) ? sp.ToString() : "0")}";
}
