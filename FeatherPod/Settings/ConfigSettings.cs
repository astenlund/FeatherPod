using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings;

internal sealed class ConfigGenerateSettings : CommandSettings
{
    [CommandOption("-s|--select")]
    [Description("Interactively select which files to generate")]
    public bool Select { get; init; }

    [CommandOption("-o|--output <PATH>")]
    [Description("Output directory (defaults to current directory)")]
    public string? OutputPath { get; init; }
}
