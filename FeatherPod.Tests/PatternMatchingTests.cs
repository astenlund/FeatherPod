using FeatherPod.Shared.Models;

namespace FeatherPod.Tests;

public class PatternMatchingTests
{
    // Copy of CliHelpers.MatchEpisodesByPattern for testing
    private static List<Episode> MatchEpisodesByPattern(List<Episode> episodes, string pattern)
    {
        // Try exact ID match first
        var exactMatch = episodes.FirstOrDefault(e => e.Id == pattern);
        if (exactMatch != null) return [exactMatch];

        // Wildcard match on filename or title (case-insensitive)
        if (pattern == "*") return episodes;

        var lower = pattern.ToLower();

        // Contains pattern: *text*
        if (pattern.StartsWith("*") && pattern.EndsWith("*"))
        {
            var contains = lower.Trim('*');
            return episodes.Where(e =>
                e.FileName.ToLower().Contains(contains) ||
                e.Title.ToLower().Contains(contains)).ToList();
        }

        // Prefix pattern: text*
        if (pattern.EndsWith("*"))
        {
            var prefix = lower[..^1];
            return episodes.Where(e =>
                e.FileName.ToLower().StartsWith(prefix) ||
                e.Title.ToLower().StartsWith(prefix)).ToList();
        }

        // Suffix pattern: *text
        if (pattern.StartsWith("*"))
        {
            var suffix = lower[1..];
            return episodes.Where(e =>
                e.FileName.ToLower().EndsWith(suffix) ||
                e.Title.ToLower().EndsWith(suffix)).ToList();
        }

        // Literal match (no wildcards) - match filename or title
        return episodes.Where(e =>
            e.FileName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
            e.Title.Equals(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<Episode> CreateTestEpisodes()
    {
        var now = DateTime.UtcNow;

        return
        [
            new Episode
            {
                Id = "abc123",
                FeedId = "test-feed",
                Title = "Episode One",
                FileName = "episode_001.mp3",
                FileSize = 1000,
                Duration = TimeSpan.FromMinutes(5),
                PublishedDate = now,
                Source = UploadSource.CLI,
                UploadedAt = now
            },
            new Episode
            {
                Id = "def456",
                FeedId = "test-feed",
                Title = "Episode Two Part1",
                FileName = "episode_002.mp3",
                FileSize = 2000,
                Duration = TimeSpan.FromMinutes(10),
                PublishedDate = now,
                Source = UploadSource.CLI,
                UploadedAt = now
            },
            new Episode
            {
                Id = "ghi789",
                FeedId = "test-feed",
                Title = "NotebookLM Overview",
                FileName = "NotebookLM_Audio.mp3",
                FileSize = 3000,
                Duration = TimeSpan.FromMinutes(15),
                PublishedDate = now,
                Source = UploadSource.CLI,
                UploadedAt = now
            },
            new Episode
            {
                Id = "jkl012",
                FeedId = "test-feed",
                Title = "Special Episode",
                FileName = "special_episode_part1.mp3",
                FileSize = 4000,
                Duration = TimeSpan.FromMinutes(20),
                PublishedDate = now,
                Source = UploadSource.CLI,
                UploadedAt = now
            }
        ];
    }

    [Fact]
    public void MatchByExactId_ShouldReturnSingleEpisode()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "abc123");

        Assert.Single(result);
        Assert.Equal("abc123", result[0].Id);
    }

    [Fact]
    public void MatchByExactId_NoMatch_ShouldReturnEmpty()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "xyz999");

        Assert.Empty(result);
    }

    [Fact]
    public void MatchAll_ShouldReturnAllEpisodes()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "*");

        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void MatchPrefix_ShouldMatchFilenameAndTitle()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "Episode*");

        // Should match "Episode One" (title) and "Episode Two Part1" (title)
        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Title == "Episode One");
        Assert.Contains(result, e => e.Title == "Episode Two Part1");
    }

    [Fact]
    public void MatchPrefix_CaseInsensitive()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "EPISODE*");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MatchSuffix_ShouldMatchFilenameAndTitle()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "*Part1");

        // Should match only "Episode Two Part1" (title) since filename ends with ".mp3" not "Part1"
        Assert.Single(result);
        Assert.Contains(result, e => e.Title == "Episode Two Part1");
    }

    [Fact]
    public void MatchContains_ShouldMatchFilenameAndTitle()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "*NotebookLM*");

        // Should match both title and filename containing "NotebookLM"
        Assert.Single(result);
        Assert.Equal("NotebookLM Overview", result[0].Title);
    }

    [Fact]
    public void MatchLiteral_ShouldMatchExactFilenameOrTitle()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "Episode One");

        Assert.Single(result);
        Assert.Equal("Episode One", result[0].Title);
    }

    [Fact]
    public void MatchLiteral_CaseInsensitive()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "episode one");

        Assert.Single(result);
        Assert.Equal("Episode One", result[0].Title);
    }

    [Fact]
    public void MatchPattern_EmptyList_ShouldReturnEmpty()
    {
        var episodes = new List<Episode>();
        var result = MatchEpisodesByPattern(episodes, "Episode*");

        Assert.Empty(result);
    }

    [Fact]
    public void MatchPattern_NoMatches_ShouldReturnEmpty()
    {
        var episodes = CreateTestEpisodes();
        var result = MatchEpisodesByPattern(episodes, "NonExistent*");

        Assert.Empty(result);
    }
}
