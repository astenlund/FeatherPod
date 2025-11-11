using FluentAssertions;
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
        id1.Should().Be(id2);
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
        id1.Should().NotBe(id2);
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
        id1.Should().NotBe(id2);
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
        id.Should().HaveLength(12);
        id.Should().MatchRegex("^[0-9a-f]{12}$");
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
        audioUrl.Should().Be("http://localhost:5000/audio/my-episode.mp3");
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
        audioUrl.Should().Contain("%20");
        audioUrl.Should().StartWith("http://localhost:5000/audio/");
    }
}
