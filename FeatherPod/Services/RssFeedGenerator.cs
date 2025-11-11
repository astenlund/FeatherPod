using System.Text;
using System.Xml;
using FeatherPod.Models;

namespace FeatherPod.Services;

public class RssFeedGenerator
{
    private readonly PodcastConfig _config;

    public RssFeedGenerator(IConfiguration configuration)
    {
        _config = configuration.GetSection("Podcast").Get<PodcastConfig>()!;
    }

    public string GenerateFeed(List<Episode> episodes)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stringWriter = new StringWriter();
        using var writer = XmlWriter.Create(stringWriter, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("rss");
        writer.WriteAttributeString("version", "2.0");
        writer.WriteAttributeString("xmlns", "itunes", null, "http://www.itunes.com/dtds/podcast-1.0.dtd");
        writer.WriteAttributeString("xmlns", "content", null, "http://purl.org/rss/1.0/modules/content/");

        writer.WriteStartElement("channel");

        // Channel metadata
        writer.WriteElementString("title", _config.Title);
        writer.WriteElementString("description", _config.Description);
        writer.WriteElementString("link", _config.BaseUrl);
        writer.WriteElementString("language", _config.Language);
        writer.WriteElementString("lastBuildDate", DateTime.UtcNow.ToString("R"));

        // iTunes specific tags
        writer.WriteStartElement("itunes", "author", null);
        writer.WriteString(_config.Author);
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "summary", null);
        writer.WriteString(_config.Description);
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "owner", null);
        writer.WriteElementString("itunes", "name", null, _config.Author);
        writer.WriteElementString("itunes", "email", null, _config.Email);
        writer.WriteEndElement();

        writer.WriteStartElement("itunes", "category", null);
        writer.WriteAttributeString("text", _config.Category);
        writer.WriteEndElement();

        writer.WriteElementString("itunes", "explicit", null, "false");

        if (!string.IsNullOrEmpty(_config.ImageUrl))
        {
            writer.WriteStartElement("itunes", "image", null);
            writer.WriteAttributeString("href", _config.ImageUrl);
            writer.WriteEndElement();

            writer.WriteStartElement("image");
            writer.WriteElementString("url", _config.ImageUrl);
            writer.WriteElementString("title", _config.Title);
            writer.WriteElementString("link", _config.BaseUrl);
            writer.WriteEndElement();
        }

        // Episodes
        foreach (var episode in episodes.OrderByDescending(e => e.PublishedDate))
        {
            WriteEpisode(writer, episode);
        }

        writer.WriteEndElement(); // channel
        writer.WriteEndElement(); // rss
        writer.WriteEndDocument();
        writer.Flush();

        return stringWriter.ToString();
    }

    private void WriteEpisode(XmlWriter writer, Episode episode)
    {
        writer.WriteStartElement("item");

        writer.WriteElementString("title", episode.Title);
        writer.WriteElementString("description", episode.Description);
        writer.WriteElementString("pubDate", episode.PublishedDate.ToString("R"));
        writer.WriteElementString("guid", episode.Id);

        // Enclosure (audio file)
        var audioUrl = episode.GetAudioUrl(_config.BaseUrl);
        writer.WriteStartElement("enclosure");
        writer.WriteAttributeString("url", audioUrl);
        writer.WriteAttributeString("length", episode.FileSize.ToString());
        writer.WriteAttributeString("type", GetMimeType(episode.FileName));
        writer.WriteEndElement();

        // iTunes specific episode tags
        writer.WriteElementString("itunes", "author", null, _config.Author);
        writer.WriteElementString("itunes", "summary", null, episode.Description);
        writer.WriteElementString("itunes", "explicit", null, "false");

        if (episode.Duration > TimeSpan.Zero)
        {
            var duration = $"{(int)episode.Duration.TotalHours:D2}:{episode.Duration.Minutes:D2}:{episode.Duration.Seconds:D2}";
            writer.WriteElementString("itunes", "duration", null, duration);
        }

        writer.WriteEndElement(); // item
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
