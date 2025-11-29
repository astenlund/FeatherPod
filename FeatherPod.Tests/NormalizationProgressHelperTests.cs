using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;

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
    [InlineData(NormalizationStage.Queued)]
    [InlineData(NormalizationStage.Downloading)]
    [InlineData(NormalizationStage.Analyzing)]
    [InlineData(NormalizationStage.Normalizing)]
    [InlineData(NormalizationStage.Uploading)]
    [InlineData(NormalizationStage.Finalizing)]
    [InlineData(NormalizationStage.Completed)]
    [InlineData(NormalizationStage.Failed)]
    public void GetStageDescription_ShouldReturnPaddedStageName(NormalizationStage stage)
    {
        var update = new ProgressUpdate { Stage = stage, ProgressPercent = 0, Message = "" };
        var result = NormalizationProgressHelper.GetStageDescription(update);

        Assert.StartsWith(stage.ToString(), result);
        Assert.Equal(stage.ToString(), result.TrimEnd());
    }

    [Fact]
    public void GetStageDescription_ShouldReturnConsistentWidth()
    {
        var stages = new[] { NormalizationStage.Unknown, NormalizationStage.Queued, NormalizationStage.Downloading, NormalizationStage.Analyzing, NormalizationStage.Normalizing, NormalizationStage.Uploading, NormalizationStage.Finalizing, NormalizationStage.Completed, NormalizationStage.Failed };
        var updates = stages.Select(s => new ProgressUpdate { Stage = s, ProgressPercent = 0, Message = "" });
        var lengths = updates.Select(u => NormalizationProgressHelper.GetStageDescription(u).Length).Distinct().ToList();

        Assert.Single(lengths);
    }

    [Fact]
    public void GetStageDescription_ShouldReturnProcessingForUnknownStage()
    {
        var update = new ProgressUpdate { Stage = NormalizationStage.Unknown, ProgressPercent = 0, Message = "" };
        var result = NormalizationProgressHelper.GetStageDescription(update);

        Assert.StartsWith("Processing", result);
    }

    [Fact]
    public void GetStageDescription_ShouldUseServerDisplayNameWhenProvided()
    {
        var update = new ProgressUpdate
        {
            Stage = NormalizationStage.Unknown,
            ProgressPercent = 50,
            Message = "",
            StageDisplayName = "NewServerStage"
        };
        var result = NormalizationProgressHelper.GetStageDescription(update);

        Assert.StartsWith("NewServerStage", result);
    }

    [Fact]
    public void GetStageDescription_ShouldUseServerMaxLengthWhenProvided()
    {
        var update = new ProgressUpdate
        {
            Stage = NormalizationStage.Queued,
            ProgressPercent = 0,
            Message = "",
            StageDisplayNameMaxLength = 20
        };
        var result = NormalizationProgressHelper.GetStageDescription(update);

        Assert.Equal(20, result.Length);
    }

    [Fact]
    public void GetStageDescription_ShouldPreferServerDisplayNameOverLocalStage()
    {
        var update = new ProgressUpdate
        {
            Stage = NormalizationStage.Queued,
            ProgressPercent = 0,
            Message = "",
            StageDisplayName = "ServerOverride"
        };
        var result = NormalizationProgressHelper.GetStageDescription(update);

        Assert.StartsWith("ServerOverride", result);
    }
}
