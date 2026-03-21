using System.Text.Json;
using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class AiTitleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void NormalizationJob_Deserialization_RoundTrips()
    {
        // Arrange
        var job = new NormalizationJob
        {
            JobId = "test",
            FeedId = "feed",
            FileName = "test.mp3",
            OriginalFileSize = 1024,
            EpisodeId = "ep1",
            Title = "Test",
            PublishedDate = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        // Act
        var json = JsonSerializer.Serialize(job, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<NormalizationJob>(json, JsonOptions)!;

        // Assert
        Assert.Equal(job.JobId, deserialized.JobId);
        Assert.Equal(job.Title, deserialized.Title);
    }

    [Fact]
    public void NormalizationJob_Deserialization_IgnoresUnknownProperties()
    {
        // Arrange - simulate an in-flight queue message with the old TitleIsUserProvided field
        var json = """
            {
                "jobId": "abc123",
                "feedId": "test-feed",
                "fileName": "episode.mp3",
                "originalFileSize": 1024,
                "episodeId": "ep123",
                "title": "Episode Title",
                "titleIsUserProvided": true,
                "publishedDate": "2026-01-01T00:00:00Z",
                "queuedAt": "2026-01-01T00:00:00Z"
            }
            """;

        // Act - should deserialize without error despite unknown property
        var job = JsonSerializer.Deserialize<NormalizationJob>(json, JsonOptions)!;

        // Assert
        Assert.Equal("abc123", job.JobId);
        Assert.Equal("Episode Title", job.Title);
    }
}
