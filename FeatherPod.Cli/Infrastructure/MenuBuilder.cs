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

                    if (i == selected)
                    {
                        if (!string.IsNullOrEmpty(option.Shortcut))
                        {
                            AnsiConsole.MarkupLine($"{prefix}[bold cyan]{option.Shortcut}[/] - {label}");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"{prefix}[bold cyan]{label}[/]");
                        }
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(option.Shortcut))
                        {
                            AnsiConsole.MarkupLine($"{prefix}[grey]{option.Shortcut}[/] - {label}");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"{prefix}[grey]{label}[/]");
                        }
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
}
