using FeatherPod.Server.Validation;

namespace FeatherPod.Tests;

public class DurationParsingTests
{
    [Fact]
    public void TryParseDuration_Hours_ReturnsCorrectTimeSpan()
    {
        // Arrange
        var input = "1h";

        // Act
        var result = InputValidation.TryParseDuration(input, out var duration);

        // Assert
        Assert.True(result);
        Assert.Equal(TimeSpan.FromHours(1), duration);
    }

    [Fact]
    public void TryParseDuration_Minutes_ReturnsCorrectTimeSpan()
    {
        // Arrange
        var input = "30m";

        // Act
        var result = InputValidation.TryParseDuration(input, out var duration);

        // Assert
        Assert.True(result);
        Assert.Equal(TimeSpan.FromMinutes(30), duration);
    }

    [Fact]
    public void TryParseDuration_Days_ReturnsCorrectTimeSpan()
    {
        // Arrange
        var input = "1d";

        // Act
        var result = InputValidation.TryParseDuration(input, out var duration);

        // Assert
        Assert.True(result);
        Assert.Equal(TimeSpan.FromDays(1), duration);
    }

    [Fact]
    public void TryParseDuration_Combined_ReturnsCorrectTimeSpan()
    {
        // Arrange
        var input = "2h30m";

        // Act
        var result = InputValidation.TryParseDuration(input, out var duration);

        // Assert
        Assert.True(result);
        Assert.Equal(new TimeSpan(0, 2, 30, 0), duration);
    }

    [Fact]
    public void TryParseDuration_AllUnits_ReturnsCorrectTimeSpan()
    {
        // Arrange
        var input = "1d2h30m";

        // Act
        var result = InputValidation.TryParseDuration(input, out var duration);

        // Assert
        Assert.True(result);
        Assert.Equal(new TimeSpan(1, 2, 30, 0), duration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("1x")]
    [InlineData("0h")]
    [InlineData("0d0h0m")]
    [InlineData("h1")]
    [InlineData("1h2")]
    [InlineData("30m2h")]
    [InlineData("2h1d")]
    [InlineData("99999999999d")]
    public void TryParseDuration_InvalidInput_ReturnsFalse(string? input)
    {
        // Arrange & Act
        var result = InputValidation.TryParseDuration(input, out var duration);

        // Assert
        Assert.False(result);
        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Fact]
    public void TryParseDuration_WithWhitespace_TrimsAndParses()
    {
        // Arrange
        var input = "  1h  ";

        // Act
        var result = InputValidation.TryParseDuration(input, out var duration);

        // Assert
        Assert.True(result);
        Assert.Equal(TimeSpan.FromHours(1), duration);
    }
}
