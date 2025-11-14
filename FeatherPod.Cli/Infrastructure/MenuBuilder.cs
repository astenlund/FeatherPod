using Spectre.Console;

namespace FeatherPod.Cli.Infrastructure;

internal class MenuBuilder<T>
{
    private string _title = "Select an option:";
    private string _hint = "(arrow keys, Enter to select, Esc to cancel)";
    private readonly List<MenuOption<T>> _options = new();
    private bool _allowCancel = true;
    private T? _cancelValue = default;

    public MenuBuilder<T> WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public MenuBuilder<T> WithHint(string hint)
    {
        _hint = hint;
        return this;
    }

    public MenuBuilder<T> AddOption(string? shortcut, string label, T value, Func<int, string>? formatter = null)
    {
        _options.Add(new MenuOption<T>(shortcut, label, value, formatter));
        return this;
    }

    public MenuBuilder<T> AllowCancel(bool allow = true, T? cancelValue = default)
    {
        _allowCancel = allow;
        _cancelValue = cancelValue;
        return this;
    }

    public T? Show()
    {
        if (_options.Count == 0)
            throw new InvalidOperationException("Menu must have at least one option");

        var selected = 0;
        Console.CursorVisible = false;

        try
        {
            while (true)
            {
                // Render menu
                AnsiConsole.Markup($"[bold]{_title}[/] ");
                AnsiConsole.MarkupLine($"[grey]{_hint}[/]");
                AnsiConsole.WriteLine();

                for (int i = 0; i < _options.Count; i++)
                {
                    var option = _options[i];
                    var prefix = i == selected ? "[cyan]>[/] " : "  ";

                    var label = option.Formatter?.Invoke(i) ?? option.Label;
                    var formattedLabel = FormatLabelWithShortcut(label, option.Shortcut, i == selected);

                    if (i == selected)
                    {
                        AnsiConsole.MarkupLine($"{prefix}{formattedLabel}");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"{prefix}{formattedLabel}");
                    }
                }

                var keyInfo = Console.ReadKey(true);

                // Check for shortcut keys first
                foreach (var option in _options)
                {
                    if (!string.IsNullOrEmpty(option.Shortcut) &&
                        keyInfo.Key.ToString().Equals(option.Shortcut, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.CursorVisible = true;
                        AnsiConsole.WriteLine();
                        return option.Value;
                    }
                }

                // Handle navigation keys
                switch (keyInfo.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = (selected - 1 + _options.Count) % _options.Count;
                        break;

                    case ConsoleKey.DownArrow:
                        selected = (selected + 1) % _options.Count;
                        break;

                    case ConsoleKey.Enter:
                        Console.CursorVisible = true;
                        AnsiConsole.WriteLine();
                        return _options[selected].Value;

                    case ConsoleKey.Escape:
                        if (_allowCancel)
                        {
                            Console.CursorVisible = true;
                            AnsiConsole.WriteLine();
                            return _cancelValue;
                        }
                        break;
                }

                // Clear for redraw (move cursor up)
                Console.SetCursorPosition(0, Console.CursorTop - _options.Count - 2);
            }
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }

    private record MenuOption<TValue>(string? Shortcut, string Label, TValue Value, Func<int, string>? Formatter);

    private static string FormatLabelWithShortcut(string label, string? shortcut, bool isSelected)
    {
        // If no shortcut, just apply standard formatting
        if (string.IsNullOrEmpty(shortcut))
        {
            return isSelected ? $"[bold cyan]{label}[/]" : $"[grey]{label}[/]";
        }

        // Check if label contains markup tags
        var hasMarkup = label.Contains('[') && label.Contains(']');

        // If selected, don't highlight the shortcut letter (entire line is bold cyan)
        if (isSelected)
        {
            return hasMarkup ? label : $"[bold cyan]{label}[/]";
        }

        // If label already has markup, just return it as-is
        // Inserting additional markup inside existing markup can break the tags
        if (hasMarkup)
        {
            return label;
        }

        // Find the shortcut character in plain text labels
        var shortcutChar = shortcut[0];
        var index = label.IndexOf(shortcutChar, StringComparison.OrdinalIgnoreCase);

        if (index == -1)
        {
            // Shortcut not found in label, use fallback format
            return $"[grey]{label}[/]";
        }

        // Split label and highlight the shortcut character
        var before = label.Substring(0, index);
        var shortcutLetter = label.Substring(index, 1);
        var after = label.Substring(index + 1);

        // For plain text labels, grey text with cyan shortcut letter
        return $"[grey]{before}[/][cyan]{shortcutLetter}[/][grey]{after}[/]";
    }
}
