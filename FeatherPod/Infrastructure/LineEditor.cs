using System.Text;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Inline text editor with cursor navigation.
/// Pre-fills the input with an initial value that the user can edit in place.
/// Uses a scrolling window to prevent display corruption when text exceeds terminal width.
/// </summary>
internal static class LineEditor
{
    /// <summary>
    /// Shows an editable text prompt with the given initial value.
    /// Supports arrow keys, Home/End, Backspace/Delete for editing.
    /// Returns the edited text, or null if cancelled (Esc) or empty.
    /// </summary>
    public static string? Edit(string prompt, string initialValue = "")
    {
        Out.Flush();

        var buffer = new StringBuilder(initialValue);
        var cursorPos = buffer.Length;
        var maxTextWidth = Math.Max(20, Console.WindowWidth - prompt.Length - 1);
        var scrollOffset = Math.Max(0, buffer.Length - maxTextWidth);

        Render(prompt, buffer, cursorPos, maxTextWidth, ref scrollOffset);

        while (true)
        {
            var key = Console.ReadKey(true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Out.WriteLineRaw();
                    var result = buffer.ToString();

                    return string.IsNullOrWhiteSpace(result) ? null : result;

                case ConsoleKey.Escape:
                    Out.WriteLineRaw();

                    return null;

                case ConsoleKey.Backspace:
                    if (cursorPos > 0)
                    {
                        buffer.Remove(cursorPos - 1, 1);
                        cursorPos--;
                        Render(prompt, buffer, cursorPos, maxTextWidth, ref scrollOffset);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (cursorPos < buffer.Length)
                    {
                        buffer.Remove(cursorPos, 1);
                        Render(prompt, buffer, cursorPos, maxTextWidth, ref scrollOffset);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursorPos > 0)
                    {
                        cursorPos--;
                        Render(prompt, buffer, cursorPos, maxTextWidth, ref scrollOffset);
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursorPos < buffer.Length)
                    {
                        cursorPos++;
                        Render(prompt, buffer, cursorPos, maxTextWidth, ref scrollOffset);
                    }
                    break;

                case ConsoleKey.Home:
                    if (cursorPos > 0)
                    {
                        cursorPos = 0;
                        Render(prompt, buffer, cursorPos, maxTextWidth, ref scrollOffset);
                    }
                    break;

                case ConsoleKey.End:
                    if (cursorPos < buffer.Length)
                    {
                        cursorPos = buffer.Length;
                        Render(prompt, buffer, cursorPos, maxTextWidth, ref scrollOffset);
                    }
                    break;

                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(cursorPos, key.KeyChar);
                        cursorPos++;
                        Render(prompt, buffer, cursorPos, maxTextWidth, ref scrollOffset);
                    }
                    break;
            }
        }
    }

    private static void Render(string prompt, StringBuilder buffer, int cursorPos, int maxTextWidth, ref int scrollOffset)
    {
        if (buffer.Length <= maxTextWidth)
        {
            scrollOffset = 0;
        }
        else if (cursorPos < scrollOffset)
        {
            scrollOffset = cursorPos;
        }
        else if (cursorPos > scrollOffset + maxTextWidth)
        {
            scrollOffset = cursorPos - maxTextWidth;
        }

        var visibleLength = Math.Min(maxTextWidth, buffer.Length - scrollOffset);

        Out.WriteRaw($"\r{prompt}{buffer.ToString(scrollOffset, visibleLength)}\e[K");

        var distFromEnd = visibleLength - (cursorPos - scrollOffset);
        if (distFromEnd > 0)
        {
            Out.WriteRaw($"\e[{distFromEnd}D");
        }
    }
}
