namespace FeatherPod.Shared;

public static class StringExtensions
{
    /// <summary>
    /// Returns the string unchanged if its length is at most <paramref name="maxLength"/>,
    /// otherwise returns the first <paramref name="maxLength"/> characters.
    /// </summary>
    public static string Truncate(this string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);

        return value.Length > maxLength ? value[..maxLength] : value;
    }
}
