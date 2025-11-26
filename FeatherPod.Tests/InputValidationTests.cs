using FeatherPod.Server.Validation;

namespace FeatherPod.Tests;

public class InputValidationTests
{
    #region IsValidFeedId Tests

    [Fact]
    public void IsValidFeedId_ValidSimple_ReturnsTrue()
    {
        Assert.True(InputValidation.IsValidFeedId("myfeed"));
    }

    [Fact]
    public void IsValidFeedId_ValidWithHyphens_ReturnsTrue()
    {
        Assert.True(InputValidation.IsValidFeedId("my-feed"));
    }

    [Fact]
    public void IsValidFeedId_ValidWithUnderscores_ReturnsTrue()
    {
        Assert.True(InputValidation.IsValidFeedId("my_feed"));
    }

    [Fact]
    public void IsValidFeedId_ValidWithNumbers_ReturnsTrue()
    {
        Assert.True(InputValidation.IsValidFeedId("feed123"));
    }

    [Fact]
    public void IsValidFeedId_ValidMixed_ReturnsTrue()
    {
        Assert.True(InputValidation.IsValidFeedId("My-Feed_123"));
    }

    [Fact]
    public void IsValidFeedId_ValidMaxLength_ReturnsTrue()
    {
        var maxLengthId = new string('a', 64);
        Assert.True(InputValidation.IsValidFeedId(maxLengthId));
    }

    [Fact]
    public void IsValidFeedId_Null_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFeedId(null));
    }

    [Fact]
    public void IsValidFeedId_Empty_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFeedId(""));
    }

    [Fact]
    public void IsValidFeedId_Whitespace_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFeedId("  "));
    }

    [Fact]
    public void IsValidFeedId_TooLong_ReturnsFalse()
    {
        var tooLongId = new string('a', 65);
        Assert.False(InputValidation.IsValidFeedId(tooLongId));
    }

    [Fact]
    public void IsValidFeedId_WithSpaces_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFeedId("my feed"));
    }

    [Fact]
    public void IsValidFeedId_WithSlash_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFeedId("my/feed"));
    }

    [Fact]
    public void IsValidFeedId_WithBackslash_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFeedId("my\\feed"));
    }

    [Fact]
    public void IsValidFeedId_WithDots_ReturnsTrue()
    {
        Assert.True(InputValidation.IsValidFeedId("my.feed"));
    }

    [Fact]
    public void IsValidFeedId_PathTraversal_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFeedId("../etc"));
    }

    #endregion

    #region IsValidFilename Tests

    [Fact]
    public void IsValidFilename_ValidSimple_ReturnsTrue()
    {
        Assert.True(InputValidation.IsValidFilename("episode.mp3"));
    }

    [Fact]
    public void IsValidFilename_ValidWithSpaces_ReturnsTrue()
    {
        Assert.True(InputValidation.IsValidFilename("my episode.mp3"));
    }

    [Fact]
    public void IsValidFilename_Null_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename(null));
    }

    [Fact]
    public void IsValidFilename_Empty_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename(""));
    }

    [Fact]
    public void IsValidFilename_Whitespace_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("  "));
    }

    [Fact]
    public void IsValidFilename_WithForwardSlash_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("path/file.mp3"));
    }

    [Fact]
    public void IsValidFilename_WithBackslash_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("path\\file.mp3"));
    }

    [Fact]
    public void IsValidFilename_PathTraversalDotDot_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("../secret.mp3"));
    }

    [Fact]
    public void IsValidFilename_PathTraversalMiddle_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("foo/../bar.mp3"));
    }

    [Fact]
    public void IsValidFilename_StartsWithSlash_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("/etc/passwd"));
    }

    [Fact]
    public void IsValidFilename_StartsWithBackslash_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("\\windows\\file"));
    }

    [Fact]
    public void IsValidFilename_WithColon_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("C:file.mp3"));
    }

    [Fact]
    public void IsValidFilename_WithNullByte_ReturnsFalse()
    {
        Assert.False(InputValidation.IsValidFilename("file\x00.mp3"));
    }

    #endregion

    #region Error Message Tests

    [Fact]
    public void GetFeedIdValidationError_Null_ReturnsRequired()
    {
        var error = InputValidation.GetFeedIdValidationError(null);
        Assert.Contains("required", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetFeedIdValidationError_Invalid_ReturnsFormat()
    {
        var error = InputValidation.GetFeedIdValidationError("a/b");
        Assert.Contains("alphanumeric", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetFilenameValidationError_Null_ReturnsRequired()
    {
        var error = InputValidation.GetFilenameValidationError(null);
        Assert.Contains("required", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetFilenameValidationError_Invalid_ReturnsInvalid()
    {
        var error = InputValidation.GetFilenameValidationError("a/b");
        Assert.Contains("invalid", error, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
