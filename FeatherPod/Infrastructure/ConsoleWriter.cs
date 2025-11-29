using Spectre.Console;
using Spectre.Console.Rendering;

namespace FeatherPod.Infrastructure;

/// <summary>
/// Manages console output with declarative blank line spacing.
/// Call BlankLine() to request a blank line before the next output.
/// This prevents double blank lines and makes spacing consistent.
/// </summary>
public class ConsoleWriter
{
    private bool _needsBlankLine;

    public static ConsoleWriter Out { get; } = new();

    public ConsoleWriter BlankLine()
    {
        _needsBlankLine = true;

        return this;
    }

    public ConsoleWriter Write(IRenderable renderable)
    {
        ArgumentNullException.ThrowIfNull(renderable);
        EmitBlankLineIfNeeded();
        AnsiConsole.Write(renderable);

        return this;
    }

    public ConsoleWriter WriteLine(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        EmitBlankLineIfNeeded();
        AnsiConsole.WriteLine(text);

        return this;
    }

    public ConsoleWriter Markup(string markup)
    {
        ArgumentException.ThrowIfNullOrEmpty(markup);
        EmitBlankLineIfNeeded();
        AnsiConsole.Markup(markup);

        return this;
    }

    public ConsoleWriter MarkupLine(string markup)
    {
        ArgumentException.ThrowIfNullOrEmpty(markup);
        EmitBlankLineIfNeeded();
        AnsiConsole.MarkupLine(markup);

        return this;
    }

    public ConsoleWriter Flush()
    {
        EmitBlankLineIfNeeded();

        return this;
    }

    /// <summary>
    /// Writes raw text directly to console without blank line handling.
    /// Use for ANSI escape sequences and cursor control.
    /// </summary>
    public ConsoleWriter WriteRaw(string text)
    {
        AnsiConsole.Write(text);

        return this;
    }

    /// <summary>
    /// Writes a line directly to console without blank line handling.
    /// Use for interactive rendering where blank line logic would interfere.
    /// </summary>
    public ConsoleWriter WriteLineRaw(string text = "")
    {
        AnsiConsole.WriteLine(text);

        return this;
    }

    /// <summary>
    /// Writes markup directly to console without blank line handling.
    /// Use for interactive rendering where blank line logic would interfere.
    /// </summary>
    public ConsoleWriter MarkupRaw(string markup)
    {
        AnsiConsole.Markup(markup);

        return this;
    }

    /// <summary>
    /// Writes markup line directly to console without blank line handling.
    /// Use for interactive rendering where blank line logic would interfere.
    /// </summary>
    public ConsoleWriter MarkupLineRaw(string markup)
    {
        AnsiConsole.MarkupLine(markup);

        return this;
    }

    public ConsoleWriter Success(string message, int indent = 0)
    {
        EmitBlankLineIfNeeded();
        var prefix = new string(' ', indent);
        AnsiConsole.MarkupLine($"{prefix}[green]✓[/] {message}");

        return this;
    }

    public ConsoleWriter Error(string message, int indent = 0)
    {
        EmitBlankLineIfNeeded();
        var prefix = new string(' ', indent);
        AnsiConsole.MarkupLine($"{prefix}[red]✗[/] {message}");

        return this;
    }

    public ConsoleWriter Warning(string message, int indent = 0)
    {
        EmitBlankLineIfNeeded();
        var prefix = new string(' ', indent);
        AnsiConsole.MarkupLine($"{prefix}[yellow]Δ[/] {message}");

        return this;
    }

    public ConsoleWriter Cancelled(int indent = 0)
    {
        EmitBlankLineIfNeeded();
        var prefix = new string(' ', indent);
        AnsiConsole.MarkupLine($"{prefix}[grey]Cancelled.[/]");

        return this;
    }

    private void EmitBlankLineIfNeeded()
    {
        if (_needsBlankLine)
        {
            AnsiConsole.WriteLine();
            _needsBlankLine = false;
        }
    }
}
