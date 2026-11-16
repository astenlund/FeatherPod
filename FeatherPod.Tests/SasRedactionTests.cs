using FeatherPod.Server.Validation;

namespace FeatherPod.Tests;

public class SasRedactionTests
{
    private const string BlobUrl = "https://acct.blob.core.windows.net/pending/job-1/audio.mp3";
    private const string SasQuery = "sv=2024-11-04&se=2026-11-16T15%3A00%3A00Z&sr=b&sp=r&sig=SECRETabc%2Bdef%3D";
    private const string Secret = "SECRETabc";

    [Fact]
    public void Redact_SasUrlInRunningText_ReplacesQuery()
    {
        // Arrange
        var text = $"InvalidUri: could not download {BlobUrl}?{SasQuery} (404).";

        // Act
        var redacted = SasRedaction.Redact(text);

        // Assert
        Assert.Equal($"InvalidUri: could not download {BlobUrl}?{SasRedaction.RedactedQuery} (404).", redacted);
    }

    [Fact]
    public void Redact_SeveralUrls_RedactsEachSasAndKeepsOthers()
    {
        // Arrange
        var plain = "https://speech.example/docs?topic=errors";
        var text = $"{BlobUrl}?{SasQuery} and {plain} and {BlobUrl}?sig=SECRETtwo&sv=1";

        // Act
        var redacted = SasRedaction.Redact(text);

        // Assert
        Assert.Equal($"{BlobUrl}?{SasRedaction.RedactedQuery} and {plain} and {BlobUrl}?{SasRedaction.RedactedQuery}", redacted);
    }

    [Fact]
    public void Redact_UrlWithoutSignature_IsUnchanged()
    {
        // Arrange
        var text = $"see {BlobUrl}?sv=2024-11-04&xsig=1 and {BlobUrl}";

        // Act
        var redacted = SasRedaction.Redact(text);

        // Assert
        Assert.Equal(text, redacted);
    }

    [Fact]
    public void Redact_JsonEscapedUrl_ReplacesQuery()
    {
        // Arrange -- a JSON serializer may write "/" as an escaped slash and "&" as a unicode escape.
        var backslash = (char)92;
        var escapedAmp = backslash + "u0026";
        var escapedUrl = $"https:{backslash}/{backslash}/acct.blob.core.windows.net{backslash}/pending{backslash}/audio.mp3?sv=1{escapedAmp}sig={Secret}";
        var text = "{\"message\":\"cannot read " + escapedUrl + "\"}";

        // Act
        var redacted = SasRedaction.Redact(text);

        // Assert
        Assert.DoesNotContain(Secret, redacted);
        Assert.Contains("audio.mp3?" + SasRedaction.RedactedQuery, redacted);
    }

    [Fact]
    public void Redact_UppercaseSignatureParameter_ReplacesQuery()
    {
        // Arrange
        var text = $"{BlobUrl}?SV=1&SIG={Secret}";

        // Act
        var redacted = SasRedaction.Redact(text);

        // Assert
        Assert.Equal($"{BlobUrl}?{SasRedaction.RedactedQuery}", redacted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no urls here")]
    public void Redact_TextWithoutUrls_IsUnchanged(string text)
    {
        // Act
        var redacted = SasRedaction.Redact(text);

        // Assert
        Assert.Equal(text, redacted);
    }
}
