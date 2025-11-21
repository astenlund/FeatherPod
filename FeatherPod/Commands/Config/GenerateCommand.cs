using System.Reflection;
using FeatherPod.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands.Config;

internal sealed class GenerateCommand : Command<ConfigGenerateSettings>
{
    private static readonly string[] AllFiles =
    [
        "appsettings.json",
        "appsettings.Dev.json",
        "appsettings.Test.json",
        "appsettings.Prod.json"
    ];

    public override int Execute(CommandContext context, ConfigGenerateSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Generate Configuration Files[/]");
        AnsiConsole.WriteLine();

        var outputPath = settings.OutputPath ?? Directory.GetCurrentDirectory();

        if (!Directory.Exists(outputPath))
        {
            AnsiConsole.MarkupLine($"[red]Output directory does not exist:[/] {outputPath}");

            return 1;
        }

        var filesToGenerate = settings.Select ? SelectFiles() : AllFiles.ToList();

        if (filesToGenerate.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No files selected.[/]");

            return 0;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var generatedCount = 0;

        foreach (var fileName in filesToGenerate)
        {
            var resourceName = $"FeatherPod.{fileName}";
            var targetPath = Path.Combine(outputPath, fileName);

            // Check if file already exists
            if (File.Exists(targetPath))
            {
                var overwrite = AnsiConsole.Confirm($"[yellow]{fileName}[/] already exists. Overwrite?", defaultValue: false);
                if (!overwrite)
                {
                    AnsiConsole.MarkupLine($"[grey]Skipped {fileName}[/]");
                    continue;
                }
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                AnsiConsole.MarkupLine($"[red]Could not find embedded resource:[/] {resourceName}");
                continue;
            }

            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            File.WriteAllText(targetPath, content);

            AnsiConsole.MarkupLine($"[green]✓[/] Generated [cyan]{fileName}[/]");
            generatedCount++;
        }

        AnsiConsole.WriteLine();

        if (generatedCount > 0)
        {
            AnsiConsole.MarkupLine($"Generated {generatedCount} file(s) to [cyan]{outputPath}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Edit these files to customize configuration, then run FeatherPod from this directory.[/]");
        }

        AnsiConsole.WriteLine();

        return 0;
    }

    private static List<string> SelectFiles()
    {
        return AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Select configuration files to generate:")
                .NotRequired()
                .PageSize(10)
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
                .AddChoices(AllFiles));
    }
}
