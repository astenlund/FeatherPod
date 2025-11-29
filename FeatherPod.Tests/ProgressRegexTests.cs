using System.Globalization;
using System.Text.RegularExpressions;

namespace FeatherPod.Tests;

/// <summary>
/// Tests for FFmpeg time= progress regex parsing.
/// Uses a copy of the regex from AudioNormalizationService/FFmpegService for testing.
/// </summary>
public partial class ProgressRegexTests
{
    /// <summary>
    /// Copy of the regex from AudioNormalizationService and FFmpegService.
    /// Pattern: time=HH:MM:SS.ms
    /// </summary>
    [GeneratedRegex("""time=(\d+):(\d+):(\d+\.\d+)""", RegexOptions.Compiled)]
    private static partial Regex ProgressRegex();

    [Theory]
    [InlineData("time=00:00:01.50", 0, 0, 1.5)]
    [InlineData("time=00:00:00.00", 0, 0, 0.0)]
    [InlineData("time=00:05:30.00", 0, 5, 30.0)]
    [InlineData("time=01:23:45.67", 1, 23, 45.67)]
    [InlineData("time=12:59:59.99", 12, 59, 59.99)]
    public void ProgressRegex_ShouldParseTimeCorrectly(string input, int expectedH, int expectedM, double expectedS)
    {
        var match = ProgressRegex().Match(input);

        Assert.True(match.Success, $"Regex should match input: {input}");
        Assert.Equal(expectedH, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(expectedM, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        Assert.Equal(expectedS, double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("frame=100 time=00:10:00.00 bitrate=128kbps", 0, 10, 0.0)]
    [InlineData("size=1234kB time=00:02:30.50 speed=1.5x", 0, 2, 30.5)]
    [InlineData("frame=50 fps=25 time=00:00:02.00 bitrate=N/A", 0, 0, 2.0)]
    public void ProgressRegex_ShouldParseTimeFromFullFFmpegOutput(string input, int expectedH, int expectedM, double expectedS)
    {
        var match = ProgressRegex().Match(input);

        Assert.True(match.Success, $"Regex should match input: {input}");
        Assert.Equal(expectedH, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        Assert.Equal(expectedM, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        Assert.Equal(expectedS, double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("random output")]
    [InlineData("time=invalid")]
    [InlineData("time=00:00")]
    [InlineData("time=:00:00.00")]
    [InlineData("time=00::00.00")]
    [InlineData("")]
    [InlineData("   ")]
    public void ProgressRegex_ShouldNotMatchInvalidInput(string input)
    {
        var match = ProgressRegex().Match(input);

        Assert.False(match.Success, $"Regex should not match input: '{input}'");
    }

    [Fact]
    public void ProgressRegex_ShouldCalculateCorrectTotalSeconds()
    {
        var input = "time=01:30:45.50";
        var match = ProgressRegex().Match(input);

        Assert.True(match.Success);

        var hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var totalSeconds = hours * 3600 + minutes * 60 + seconds;

        Assert.Equal(5445.5, totalSeconds);
    }

    [Fact]
    public void ProgressRegex_ShouldCalculateCorrectPercentage()
    {
        var input = "time=00:02:30.00";
        var totalDuration = TimeSpan.FromMinutes(5);
        var match = ProgressRegex().Match(input);

        Assert.True(match.Success);

        var hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var currentSeconds = hours * 3600 + minutes * 60 + seconds;
        var percent = (int)Math.Min(100, (currentSeconds / totalDuration.TotalSeconds) * 100);

        Assert.Equal(50, percent);
    }
}
