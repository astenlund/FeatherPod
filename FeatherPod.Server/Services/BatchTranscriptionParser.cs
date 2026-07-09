using System.Text.Json;

namespace FeatherPod.Server.Services;

/// <summary>
/// Parses an Azure Speech batch transcription result file (<c>/speechtotext/v3.2/transcriptions</c>)
/// into <see cref="DiarizedSegment"/>s. The batch shape differs from Fast: phrase text lives under
/// <c>nBest[0].display</c>, and <c>offsetInTicks</c>/<c>durationInTicks</c> are 100ns ticks returned
/// as floats (e.g. 400000.0), so they are read with <c>GetDouble()</c> + cast rather than
/// <c>GetInt64()</c>, which throws <see cref="FormatException"/> on floats.
/// </summary>
public static class BatchTranscriptionParser
{
    /// <summary>
    /// Parse the root element of a batch transcription result. Returns an empty list if
    /// <c>recognizedPhrases[]</c> is missing or contains only empty/whitespace phrases;
    /// phrases missing the tick fields are skipped.
    /// </summary>
    public static List<DiarizedSegment> Parse(JsonElement root)
    {
        var segments = new List<DiarizedSegment>();

        if (!root.TryGetProperty("recognizedPhrases", out var phrases) || phrases.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var phrase in phrases.EnumerateArray())
        {
            if (!phrase.TryGetProperty("offsetInTicks", out var offsetEl) || !phrase.TryGetProperty("durationInTicks", out var durationEl))
            {
                continue;
            }

            var display = phrase.GetProperty("nBest")[0].GetProperty("display").GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(display))
            {
                continue;
            }

            segments.Add(new DiarizedSegment((long)offsetEl.GetDouble(), (long)durationEl.GetDouble(), TranscriptionSpeakerLabel.FromPhrase(phrase), display));
        }

        return segments;
    }
}
