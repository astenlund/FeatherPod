using FeatherPod.Models;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class EpisodeTests
{
    [Fact]
    public void GenerateId_ShouldReturnConsistentId_ForSameInput()
    {
        // Arrange
        var fileName = "test-audio.mp3";
        var fileSize = 12345L;

        // Act
        var id1 = Episode.GenerateId(fileName, fileSize);
        var id2 = Episode.GenerateId(fileName, fileSize);

        // Assert
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GenerateId_ShouldReturnDifferentIds_ForDifferentFileNames()
    {
        // Arrange
        var fileSize = 12345L;

        // Act
        var id1 = Episode.GenerateId("file1.mp3", fileSize);
        var id2 = Episode.GenerateId("file2.mp3", fileSize);

        // Assert
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GenerateId_ShouldReturnDifferentIds_ForDifferentFileSizes()
    {
        // Arrange
        var fileName = "test.mp3";

        // Act
        var id1 = Episode.GenerateId(fileName, 1000L);
        var id2 = Episode.GenerateId(fileName, 2000L);

        // Assert
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GenerateId_ShouldReturn12CharacterHexString()
    {
        // Arrange
        var fileName = "test.mp3";
        var fileSize = 12345L;

        // Act
        var id = Episode.GenerateId(fileName, fileSize);

        // Assert
        Assert.Equal(12, id.Length);
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void GetAudioUrl_ShouldReturnCorrectUrl()
    {
        // Arrange
        var episode = new Episode
        {
            Id = "abc123def456",
            FileName = "my-episode.mp3",
            Title = "My Episode"
        };
        var baseUrl = "http://localhost:5000";

        // Act
        var audioUrl = episode.GetAudioUrl(baseUrl);

        // Assert
        Assert.Equal("http://localhost:5000/audio/my-episode.mp3", audioUrl);
    }

    [Fact]
    public void GetAudioUrl_ShouldEscapeSpecialCharacters()
    {
        // Arrange
        var episode = new Episode
        {
            Id = "abc123def456",
            FileName = "my episode with spaces.mp3",
            Title = "My Episode"
        };
        var baseUrl = "http://localhost:5000";

        // Act
        var audioUrl = episode.GetAudioUrl(baseUrl);

        // Assert
        Assert.Contains("%20", audioUrl);
        Assert.StartsWith("http://localhost:5000/audio/", audioUrl);
    }
}
