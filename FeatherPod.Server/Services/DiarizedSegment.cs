namespace FeatherPod.Server.Services;

/// <summary>
/// A single diarized speech segment produced by Azure Speech batch transcription.
/// </summary>
public record DiarizedSegment(long OffsetTicks, long DurationTicks, string SpeakerId, string Text);
