namespace FeatherPod.Services;

public interface IBlobStorageService
{
    Task InitializeAsync();
    Task UploadAudioAsync(string fileName, string filePath);
    Task<Stream> DownloadAudioAsync(string fileName);
    Task<bool> AudioExistsAsync(string fileName);
    Task DeleteAudioAsync(string fileName);
    Task<List<string>> ListAudioFilesAsync();
    Task<long> GetAudioFileSizeAsync(string fileName);
    Task SaveMetadataAsync(string metadataJson);
    Task<string?> LoadMetadataAsync();
    Task<string> DownloadAudioToTempAsync(string fileName);
}
