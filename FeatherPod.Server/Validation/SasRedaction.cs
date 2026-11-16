using System.Text.RegularExpressions;

namespace FeatherPod.Server.Validation;

/// <summary>
/// Strips SAS tokens from free text before it reaches logs or persisted error fields.
/// Any URL whose query string carries a <c>sig</c> parameter has its whole query replaced
/// with <see cref="RedactedQuery"/>. Tolerates JSON-escaped forms (<c>https:\/\/</c>, <c>\u0026sig=</c>),
/// since the text typically comes from a service's JSON error body.
/// </summary>
internal static partial class SasRedaction
{
    public const string RedactedQuery = "<redacted>";

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return UrlWithQuery().Replace(text, match =>
        {
            var query = match.Groups["query"];

            if (!SignatureParameter().IsMatch(query.Value))
            {
                return match.Value;
            }

            return string.Concat(match.Value.AsSpan(0, query.Index - match.Index), RedactedQuery);
        });
    }

    [GeneratedRegex("""https?:(?:\\?/){2}[^\s"'<>?]*\?(?<query>[^\s"'<>]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlWithQuery();

    // "sig" not preceded by a letter, so "&sig=", "?sig=" and JSON's "\u0026sig=" match while "xsig=" does not.
    [GeneratedRegex("(?<![a-z])sig=", RegexOptions.IgnoreCase)]
    private static partial Regex SignatureParameter();
}
