using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

/// <summary>
/// Tests for the NormalizationProgressHelper shared formatting utilities.
/// </summary>
public class NormalizationProgressHelperTests
{
    [Fact]
    public void FormatPosition_ShouldFormatMinutesAndSeconds()
    {
        var result = NormalizationProgressHelper.FormatPosition(
            TimeSpan.FromMinutes(2.5),
            TimeSpan.FromMinutes(5));

        Assert.Equal("02:30 / 05:00", result);
    }

    [Fact]
    public void FormatPosition_ShouldFormatWithLeadingZeros()
    {
        var result = NormalizationProgressHelper.FormatPosition(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(1));

        Assert.Equal("00:05 / 01:00", result);
    }

    [Fact]
    public void FormatPosition_ShouldFormatHoursWhenNeeded()
    {
        var result = NormalizationProgressHelper.FormatPosition(
            TimeSpan.FromHours(1.5),
            TimeSpan.FromHours(2));

        Assert.Equal("1:30:00 / 2:00:00", result);
    }

    [Fact]
    public void FormatPosition_ShouldFormatCurrentWithoutHoursWhenUnderOneHour()
    {
        // When current is under 1 hour, it shows mm:ss format
        // When total is 1+ hours, it shows h:mm:ss format
        var result = NormalizationProgressHelper.FormatPosition(
            TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1.5));

        Assert.Equal("30:00 / 1:30:00", result);
    }

    [Fact]
    public void FormatPosition_ShouldReturnEmptyForNullCurrent()
    {
        var result = NormalizationProgressHelper.FormatPosition(null, TimeSpan.FromMinutes(5));

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatPosition_ShouldReturnEmptyForNullTotal()
    {
        var result = NormalizationProgressHelper.FormatPosition(TimeSpan.FromMinutes(2), null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatPosition_ShouldReturnEmptyForBothNull()
    {
        var result = NormalizationProgressHelper.FormatPosition(null, null);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatPosition_ShouldReturnEmptyForZeroTotal()
    {
        var result = NormalizationProgressHelper.FormatPosition(
            TimeSpan.FromMinutes(2),
            TimeSpan.Zero);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatTime_ShouldFormatMinutesAndSeconds()
    {
        var result = NormalizationProgressHelper.FormatTime(TimeSpan.FromMinutes(5.5));

        Assert.Equal("05:30", result);
    }

    [Fact]
    public void FormatTime_ShouldFormatWithHoursWhenNeeded()
    {
        var result = NormalizationProgressHelper.FormatTime(TimeSpan.FromHours(2.5));

        Assert.Equal("2:30:00", result);
    }

    [Theory]
    [InlineData("Queued", "Queued")]
    [InlineData("Downloading", "Downloading")]
    [InlineData("Analyzing", "Analyzing")]
    [InlineData("Normalizing", "Normalizing")]
    [InlineData("Uploading", "Uploading")]
    [InlineData("Finalizing", "Finalizing")]
    [InlineData("Completed", "Complete")]
    [InlineData("Failed", "Failed")]
    public void GetStageDescription_ShouldMapKnownStagesCorrectly(string stage, string expected)
    {
        var result = NormalizationProgressHelper.GetStageDescription(stage);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("RandomStage")]
    public void GetStageDescription_ShouldReturnProcessingForUnknownStages(string stage)
    {
        var result = NormalizationProgressHelper.GetStageDescription(stage);

        Assert.Equal("Processing", result);
    }

    [Fact]
    public void GetStageDescription_ShouldReturnProcessingForNull()
    {
        var result = NormalizationProgressHelper.GetStageDescription(null);

        Assert.Equal("Processing", result);
    }
}
