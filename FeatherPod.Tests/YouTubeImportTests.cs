using System.Text.Json;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class YouTubeImportTests
{
    public class EpisodeIdGeneration
    {
        [Fact]
        public void GenerateYouTubeId_IsDeterministic()
        {
            // Arrange & Act
            var id1 = Episode.GenerateYouTubeId("test-feed", "dQw4w9WgXcQ", "audio");
            var id2 = Episode.GenerateYouTubeId("test-feed", "dQw4w9WgXcQ", "audio");

            // Assert
            Assert.Equal(id1, id2);
        }

        [Fact]
        public void GenerateYouTubeId_DiffersForDifferentFormats()
        {
            // Arrange & Act
            var audioId = Episode.GenerateYouTubeId("test-feed", "dQw4w9WgXcQ", "audio");
            var videoId = Episode.GenerateYouTubeId("test-feed", "dQw4w9WgXcQ", "video");

            // Assert
            Assert.NotEqual(audioId, videoId);
        }

        [Fact]
        public void GenerateYouTubeId_DiffersForDifferentFeeds()
        {
            // Arrange & Act
            var id1 = Episode.GenerateYouTubeId("feed-a", "dQw4w9WgXcQ", "audio");
            var id2 = Episode.GenerateYouTubeId("feed-b", "dQw4w9WgXcQ", "audio");

            // Assert
            Assert.NotEqual(id1, id2);
        }

        [Fact]
        public void GenerateYouTubeId_DiffersForDifferentVideos()
        {
            // Arrange & Act
            var id1 = Episode.GenerateYouTubeId("test-feed", "dQw4w9WgXcQ", "audio");
            var id2 = Episode.GenerateYouTubeId("test-feed", "xvFZjo5PgG0", "audio");

            // Assert
            Assert.NotEqual(id1, id2);
        }

        [Fact]
        public void GenerateYouTubeId_Is12CharsLowerHex()
        {
            // Arrange & Act
            var id = Episode.GenerateYouTubeId("test-feed", "dQw4w9WgXcQ", "audio");

            // Assert
            Assert.Equal(12, id.Length);
            Assert.Matches("^[0-9a-f]{12}$", id);
        }

        [Fact]
        public void GenerateYouTubeId_DiffersFromFileBasedId()
        {
            // Arrange & Act
            var youtubeId = Episode.GenerateYouTubeId("test-feed", "dQw4w9WgXcQ", "audio");
            var fileId = Episode.GenerateId("test-feed", "dQw4w9WgXcQ.m4a", 12345);

            // Assert -- different hash inputs, should produce different IDs
            Assert.NotEqual(youtubeId, fileId);
        }
    }

    public class ProgressMapping
    {
        [Theory]
        [InlineData(0, 5)]
        [InlineData(50, 47)]
        [InlineData(100, 90)]
        public void YtDlpProgressMapsToJobPercent(double ytDlpPercent, int expectedJobPercent)
        {
            // Arrange & Act -- mirrors YouTubeDownloadService mapping: 5 + (percent * 0.85)
            var jobPercent = 5 + (int)(ytDlpPercent * 0.85);

            // Assert
            Assert.Equal(expectedJobPercent, jobPercent);
        }
    }

    public class UploadSourceSerialization
    {
        [Fact]
        public void YouTubeUploadSource_HasExpectedValue()
        {
            // Arrange & Act
            var source = UploadSource.YouTube;

            // Assert
            Assert.Equal("YouTube", source.ToString());
        }

        [Fact]
        public void YouTubeUploadSource_RoundTrips()
        {
            // Arrange & Act
            var parsed = Enum.Parse<UploadSource>("YouTube");

            // Assert
            Assert.Equal(UploadSource.YouTube, parsed);
        }
    }

    public class MediaSourceTests
    {
        [Fact]
        public void Episode_WithoutMediaSource_DefaultsToNull()
        {
            // Arrange & Act
            var episode = new Episode
            {
                Id = "test",
                FeedId = "feed",
                Title = "Test",
                FileName = "test.mp3",
                FileSize = 1024,
                Source = UploadSource.Browser,
                UploadedAt = DateTime.UtcNow,
            };

            // Assert
            Assert.Null(episode.MediaSource);
        }

        [Fact]
        public void Episode_WithMediaSource_PreservesValue()
        {
            // Arrange & Act
            var episode = new Episode
            {
                Id = "test",
                FeedId = "feed",
                Title = "YouTube Video Title",
                FileName = "dQw4w9WgXcQ.m4a",
                FileSize = 1024,
                Source = UploadSource.Browser,
                MediaSource = MediaSource.YouTube,
                UploadedAt = DateTime.UtcNow,
            };

            // Assert
            Assert.Equal(MediaSource.YouTube, episode.MediaSource);
        }

        [Fact]
        public void Deserialization_WithoutMediaSource_DefaultsToNull()
        {
            // Arrange - JSON from before MediaSource existed
            var json = """
                {
                    "Id": "abc123",
                    "FeedId": "test-feed",
                    "Title": "Old Episode",
                    "FileName": "old.mp3",
                    "FileSize": 1024,
                    "Source": 1,
                    "UploadedAt": "2026-01-01T00:00:00Z"
                }
                """;

            // Act
            var episode = JsonSerializer.Deserialize<Episode>(json)!;

            // Assert
            Assert.Null(episode.MediaSource);
        }

        [Fact]
        public void Deserialization_WithMediaSource_RoundTrips()
        {
            // Arrange
            var episode = new Episode
            {
                Id = "test",
                FeedId = "feed",
                Title = "Test",
                FileName = "dQw4w9WgXcQ.m4a",
                FileSize = 1024,
                Source = UploadSource.Browser,
                MediaSource = MediaSource.YouTube,
                UploadedAt = DateTime.UtcNow,
            };

            // Act
            var json = JsonSerializer.Serialize(episode);
            var deserialized = JsonSerializer.Deserialize<Episode>(json)!;

            // Assert
            Assert.Equal(MediaSource.YouTube, deserialized.MediaSource);
        }
    }

    public class NormalizationStageDownloading
    {
        [Fact]
        public void DownloadingStage_HasExplicitValue()
        {
            // Arrange & Act
            var value = (int)NormalizationStage.Downloading;

            // Assert
            Assert.Equal(10, value);
        }

        [Fact]
        public void DownloadingStage_DoesNotShiftExistingValues()
        {
            // Arrange & Act & Assert -- verify existing ordinals are unchanged
            Assert.Equal(0, (int)NormalizationStage.Unknown);
            Assert.Equal(1, (int)NormalizationStage.Queued);
            Assert.Equal(2, (int)NormalizationStage.Preparing);
            Assert.Equal(3, (int)NormalizationStage.Analyzing);
            Assert.Equal(4, (int)NormalizationStage.Normalizing);
            Assert.Equal(5, (int)NormalizationStage.Finishing);
            Assert.Equal(10, (int)NormalizationStage.Downloading);
        }
    }
}
