using FeatherPod.Shared;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class StringExtensionsTests
{
    [Fact]
    public void Truncate_ShorterThanMax_ReturnsOriginal()
    {
        // Arrange
        var input = "hello";

        // Act
        var result = input.Truncate(10);

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Truncate_EqualToMax_ReturnsOriginal()
    {
        // Arrange
        var input = "hello";

        // Act
        var result = input.Truncate(5);

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Truncate_LongerThanMax_ReturnsFirstMaxChars()
    {
        // Arrange
        var input = "hello world";

        // Act
        var result = input.Truncate(5);

        // Assert
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Truncate_EmptyString_ReturnsEmpty()
    {
        // Arrange & Act
        var result = string.Empty.Truncate(10);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Truncate_NullValue_Throws()
    {
        // Arrange
        string? input = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => input!.Truncate(5));
    }

    [Fact]
    public void Truncate_NegativeMaxLength_Throws()
    {
        // Arrange
        var input = "hello";

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => input.Truncate(-1));
    }
}
