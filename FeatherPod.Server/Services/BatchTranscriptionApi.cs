namespace FeatherPod.Server.Services;

/// <summary>
/// String constants defined by the Azure Speech batch transcription REST API (v3.2):
/// terminal job statuses and result file kinds. Distinct from
/// <c>FeatherPod.Shared.Models.TranscriptionStatuses</c>, which tracks FeatherPod job
/// entity statuses, not Azure batch API statuses.
/// </summary>
internal static class BatchTranscriptionApi
{
    public const string SucceededStatus = "Succeeded";
    public const string FailedStatus = "Failed";
    public const string TranscriptionFileKind = "Transcription";
}
