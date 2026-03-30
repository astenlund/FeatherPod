namespace FeatherPod.Shared;

public static class AudioHelper
{
    public static string GetMimeType(string fileNameOrExtension)
    {
        return Path.GetExtension(fileNameOrExtension).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".m4a" or ".m4b" => "audio/mp4",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            ".opus" => "audio/opus",
            ".wma" => "audio/x-ms-wma",
            _ => "application/octet-stream"
        };
    }
}
