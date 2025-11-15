using System.Text;
using System.Xml;
using FeatherPod.Models;

namespace FeatherPod.Services;

public class RssFeedGenerator
{
    public static string GenerateFeed(FeedConfig feedConfig, string baseUrl, List<Episode> episodes)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stringWriter = new Utf8StringWriter();
        using var writer = XmlWriter.Create(stringWriter, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("rss");
        writer.WriteAttributeString("version", "2.0");
        writer.WriteAttributeString("xmlns", "itunes", null, "http://www.itunes.com/dtds/podcast-1.0.dtd");
        writer.WriteAttributeString("xmlns", "content", null, "http://purl.org/rss/1.0/modules/content/");

        writer.WriteStartElement("channel");

        // Channel metadata
        writer.WriteElementString("title", feedConfig.Title);
        writer.WriteElementString("description", feedConfig.Description ?? string.Empty);
        writer.WriteElementString("link", $"{baseUrl}/{feedConfig.Id}/feed.xml");
        writer.WriteElementString("language", feedConfig.Language);
        writer.WriteElementString("lastBuildDate", DateTime.UtcNow.ToString("R"));

        // iTunes specific tags
        writer.WriteStartElement("itunes", "author", null);
        writer.WriteString(feedConfig.Author);
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "summary", null);
        writer.WriteString(feedConfig.Description ?? string.Empty);
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "owner", null);
        writer.WriteElementString("itunes", "name", null, feedConfig.Author);
        if (!string.IsNullOrEmpty(feedConfig.Email))
        {
            writer.WriteElementString("itunes", "email", null, feedConfig.Email);
        }
        writer.WriteEndElement();

        if (!string.IsNullOrEmpty(feedConfig.Category))
        {
            writer.WriteStartElement("itunes", "category", null);
            writer.WriteAttributeString("text", feedConfig.Category);
            writer.WriteEndElement();
        }

        writer.WriteElementString("itunes", "explicit", null, "false");

        if (!string.IsNullOrEmpty(feedConfig.ImageUrl))
        {
            var imageUrl = GetImageUrlWithVersion(feedConfig, baseUrl);

            writer.WriteStartElement("itunes", "image", null);
            writer.WriteAttributeString("href", imageUrl);
            writer.WriteEndElement();

            writer.WriteStartElement("image");
            writer.WriteElementString("url", imageUrl);
            writer.WriteElementString("title", feedConfig.Title);
            writer.WriteElementString("link", $"{baseUrl}/{feedConfig.Id}/feed.xml");
            writer.WriteEndElement();
        }

        // Episodes
        foreach (var episode in episodes.OrderByDescending(e => e.PublishedDate))
        {
            WriteEpisode(writer, episode, feedConfig, baseUrl);
        }

        writer.WriteEndElement(); // channel
        writer.WriteEndElement(); // rss
        writer.WriteEndDocument();
        writer.Flush();

        return stringWriter.ToString();
    }

    private static void WriteEpisode(XmlWriter writer, Episode episode, FeedConfig feedConfig, string baseUrl)
    {
        writer.WriteStartElement("item");

        writer.WriteElementString("title", episode.Title);
        writer.WriteElementString("description", episode.Description ?? string.Empty);
        writer.WriteElementString("pubDate", episode.PublishedDate.ToString("R"));
        writer.WriteElementString("guid", episode.Id);

        // Enclosure (audio file)
        var audioUrl = episode.GetAudioUrl(baseUrl);
        writer.WriteStartElement("enclosure");
        writer.WriteAttributeString("url", audioUrl);
        writer.WriteAttributeString("length", episode.FileSize.ToString());
        writer.WriteAttributeString("type", GetMimeType(episode.FileName));
        writer.WriteEndElement();

        // iTunes specific episode tags
        writer.WriteElementString("itunes", "author", null, feedConfig.Author);
        writer.WriteElementString("itunes", "summary", null, episode.Description ?? string.Empty);
        writer.WriteElementString("itunes", "explicit", null, "false");

        if (episode.Duration > TimeSpan.Zero)
        {
            var duration = $"{(int)episode.Duration.TotalHours:D2}:{episode.Duration.Minutes:D2}:{episode.Duration.Seconds:D2}";
            writer.WriteElementString("itunes", "duration", null, duration);
        }

        writer.WriteEndElement(); // item
    }

    private static string GetImageUrlWithVersion(FeedConfig feedConfig, string baseUrl)
    {
        // Use feed-specific icon from blob storage
        var iconUrl = $"{baseUrl}/{feedConfig.Id}/icon.png";

        if (string.IsNullOrEmpty(feedConfig.ImageVersion))
        {
            return iconUrl;
        }

        // Append version as query parameter for cache busting
        return $"{iconUrl}?v={feedConfig.ImageVersion}";
    }

    private static string GetMimeType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".aac" => "audio/aac",
            _ => "audio/mpeg"
        };
    }
}

// Helper class to make StringWriter report UTF-8 encoding
internal sealed class Utf8StringWriter : StringWriter
{
    public override Encoding Encoding => Encoding.UTF8;
}
