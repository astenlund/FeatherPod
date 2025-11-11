using FeatherPod.Services;

namespace FeatherPod.Tests;

/// <summary>
/// Test implementation of blob storage that uses local file system instead of Azure Blob Storage.
/// This allows tests to run without requiring Azure storage or emulator.
/// </summary>
public class TestBlobStorageService : IBlobStorageService
{
    private readonly string _audioPath;
    private readonly string _metadataPath;
    public TestBlobStorageService(string testDirectory)
    {
        _audioPath = Path.Combine(testDirectory, "audio");
        _metadataPath = Path.Combine(testDirectory, "metadata.json");

        Directory.CreateDirectory(_audioPath);
    }

    public async Task InitializeAsync()
    {
        // Do nothing - we don't need to create actual containers
        await Task.CompletedTask;
    }

    public async Task UploadAudioAsync(string fileName, string filePath)
    {
        var destPath = Path.Combine(_audioPath, fileName);
        File.Copy(filePath, destPath, overwrite: true);
        await Task.CompletedTask;
    }

    public async Task<Stream> DownloadAudioAsync(string fileName)
    {
        var filePath = Path.Combine(_audioPath, fileName);
        var fileStream = File.OpenRead(filePath);
        return await Task.FromResult<Stream>(fileStream);
    }

    public async Task<bool> AudioExistsAsync(string fileName)
    {
        var filePath = Path.Combine(_audioPath, fileName);
        return await Task.FromResult(File.Exists(filePath));
    }

    public async Task DeleteAudioAsync(string fileName)
    {
        var filePath = Path.Combine(_audioPath, fileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        await Task.CompletedTask;
    }

    public async Task<List<string>> ListAudioFilesAsync()
    {
        if (!Directory.Exists(_audioPath))
        {
            return new List<string>();
        }

        var files = Directory.GetFiles(_audioPath)
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .ToList();

        return await Task.FromResult(files);
    }

    public async Task<long> GetAudioFileSizeAsync(string fileName)
    {
        var filePath = Path.Combine(_audioPath, fileName);
        var fileInfo = new FileInfo(filePath);
        return await Task.FromResult(fileInfo.Length);
    }

    public async Task SaveMetadataAsync(string metadataJson)
    {
        await File.WriteAllTextAsync(_metadataPath, metadataJson);
    }

    public async Task<string?> LoadMetadataAsync()
    {
        if (!File.Exists(_metadataPath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(_metadataPath);
    }

    public async Task<string> DownloadAudioToTempAsync(string fileName)
    {
        var sourcePath = Path.Combine(_audioPath, fileName);
        var tempPath = Path.Combine(Path.GetTempPath(), fileName);
        File.Copy(sourcePath, tempPath, overwrite: true);
        return await Task.FromResult(tempPath);
    }
}
