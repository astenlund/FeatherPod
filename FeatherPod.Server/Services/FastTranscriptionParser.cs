using System.Text.Json;

namespace FeatherPod.Server.Services;

/// <summary>
/// Parses an Azure Speech Fast Transcription response (<c>/speechtotext/transcriptions:transcribe</c>)
/// into <see cref="DiarizedSegment"/>s. The Fast response shape is intentionally different from
/// batch: phrases have a flat <c>text</c> field (no <c>nBest[]</c>), times are in milliseconds
/// (not 100ns ticks), and <c>speaker</c> is a JSON number, not a string.
/// </summary>
public static class FastTranscriptionParser
{
    /// <summary>
    /// Parse the root element of a Fast Transcription response. Returns an empty list if
    /// <c>phrases[]</c> is missing or contains only empty/whitespace text.
    /// </summary>
    public static List<DiarizedSegment> Parse(JsonElement root)
    {
        var segments = new List<DiarizedSegment>();

        if (!root.TryGetProperty("phrases", out var phrases) || phrases.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var phrase in phrases.EnumerateArray())
        {
            // Speaker is a JSON number (e.g. "speaker": 0). ToString() handles both numeric and
            // string tokens; GetString() throws on numeric tokens. Missing speaker => "0".
            var speaker = phrase.TryGetProperty("speaker", out var sp) ? sp.ToString() : "0";
            var text = phrase.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(text)
                || !phrase.TryGetProperty("offsetMilliseconds", out var offsetEl)
                || !phrase.TryGetProperty("durationMilliseconds", out var durationEl))
            {
                continue;
            }

            segments.Add(new DiarizedSegment(
                offsetEl.GetInt64() * TimeSpan.TicksPerMillisecond,
                durationEl.GetInt64() * TimeSpan.TicksPerMillisecond,
                $"Speaker {speaker}",
                text));
        }

        return segments;
    }
}
