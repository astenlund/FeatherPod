using System.Text.Json.Serialization;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Result from FFmpeg loudnorm filter analysis (Pass 1).
/// JSON output format from -af loudnorm=print_format=json
/// </summary>
internal class LoudnessAnalysis
{
    [JsonPropertyName("input_i")]
    public string InputI { get; init; } = string.Empty;

    [JsonPropertyName("input_tp")]
    public string InputTp { get; init; } = string.Empty;

    [JsonPropertyName("input_lra")]
    public string InputLra { get; init; } = string.Empty;

    [JsonPropertyName("input_thresh")]
    public string InputThresh { get; init; } = string.Empty;

    [JsonPropertyName("output_i")]
    public string OutputI { get; init; } = string.Empty;

    [JsonPropertyName("output_tp")]
    public string OutputTp { get; init; } = string.Empty;

    [JsonPropertyName("output_lra")]
    public string OutputLra { get; init; } = string.Empty;

    [JsonPropertyName("output_thresh")]
    public string OutputThresh { get; init; } = string.Empty;

    [JsonPropertyName("normalization_type")]
    public string NormalizationType { get; init; } = string.Empty;

    [JsonPropertyName("target_offset")]
    public string TargetOffset { get; init; } = string.Empty;
}
