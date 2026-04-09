using FeatherPod.Shared;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class AudioHelperTests
{
    [Theory]
    [InlineData("clip.mp3", "audio/mpeg")]
    [InlineData("clip.m4a", "audio/mp4")]
    [InlineData("book.m4b", "audio/mp4")]
    [InlineData("clip.wav", "audio/wav")]
    [InlineData("clip.ogg", "audio/ogg")]
    [InlineData("clip.flac", "audio/flac")]
    [InlineData("clip.aac", "audio/aac")]
    [InlineData("clip.opus", "audio/opus")]
    [InlineData("clip.wma", "audio/x-ms-wma")]
    public void GetMimeType_AudioExtensions_ReturnsAudioMimeType(string filename, string expected)
    {
        // Arrange & Act
        var result = AudioHelper.GetMimeType(filename);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.webm", "video/webm")]
    [InlineData("clip.mkv", "video/x-matroska")]
    public void GetMimeType_VideoExtensions_ReturnsVideoMimeType(string filename, string expected)
    {
        // Arrange & Act
        var result = AudioHelper.GetMimeType(filename);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("CLIP.MP3", "audio/mpeg")]
    [InlineData("Clip.Mp4", "video/mp4")]
    [InlineData("clip.MKV", "video/x-matroska")]
    public void GetMimeType_ExtensionCaseInsensitive(string filename, string expected)
    {
        // Arrange & Act
        var result = AudioHelper.GetMimeType(filename);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetMimeType_UnknownExtension_ReturnsOctetStream()
    {
        // Arrange & Act
        var result = AudioHelper.GetMimeType("clip.xyz");

        // Assert
        Assert.Equal("application/octet-stream", result);
    }

    [Fact]
    public void GetMimeType_NoExtension_ReturnsOctetStream()
    {
        // Arrange & Act
        var result = AudioHelper.GetMimeType("clip");

        // Assert
        Assert.Equal("application/octet-stream", result);
    }
}
