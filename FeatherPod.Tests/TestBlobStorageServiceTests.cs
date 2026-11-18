namespace FeatherPod.Tests;

/// <summary>
/// The file-based test double's blob versions, which the ownership tests rely on to simulate another writer of an audio path.
/// </summary>
[Collection("Sequential")]
public sealed class TestBlobStorageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"FeatherPodTestBlob_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the test's temp tree
        }
    }

    [Fact]
    public void GetAudioETag_DirectRewriteRightAfterAnObservedVersion_IsANewVersion()
    {
        // Arrange: a direct write simulates the Function overwriting the path; rewrites follow within the same timestamp tick
        var blob = new TestBlobStorageService(_root);
        var path = Path.Combine(_root, "feed", "audio", "show.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var missed = 0;

        // Act
        for (var i = 0; i < 200; i++)
        {
            File.WriteAllBytes(path, [1, 2, 3]);
            var before = blob.GetAudioETag("feed", "show.mp3");
            File.WriteAllBytes(path, i % 2 == 0 ? [5, 5, 5] : [1, 2, 3]);
            if (blob.GetAudioETag("feed", "show.mp3") == before)
            {
                missed++;
            }
        }

        // Assert: every rewrite, same length or even same content, reads as a new blob version
        Assert.Equal(0, missed);
    }

    [Fact]
    public async Task GetAudioETag_UnchangedBlob_KeepsTheVersionItsWriteReported()
    {
        // Arrange
        var blob = new TestBlobStorageService(_root);
        var source = Path.Combine(_root, "source.mp3");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(source, [1, 2, 3]);

        // Act
        var uploaded = await blob.UploadAudioAsync("feed", "show.mp3", source);

        // Assert: reads without a write in between report the uploaded version, as a download would
        Assert.Equal(uploaded, blob.GetAudioETag("feed", "show.mp3"));
        Assert.Equal(uploaded, blob.GetAudioETag("feed", "show.mp3"));
        Assert.Equal(uploaded, (await blob.DownloadAudioToTempAsync("feed", "show.mp3")).ETag);
    }
}
