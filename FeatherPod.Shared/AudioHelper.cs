namespace FeatherPod.Shared;

public static class AudioHelper
{
    public static string GetMimeType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            _ => "application/octet-stream"
        };
    }
}
