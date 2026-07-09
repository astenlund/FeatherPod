using System.ComponentModel;
using Spectre.Console.Cli;

namespace FeatherPod.Settings.Episode;

internal sealed class NoteSetSettings : NoteSettings
{
    [CommandOption("-n|--note <NOTE>")]
    [Description("Note text (prompts interactively when omitted)")]
    public string? Note { get; init; }
}
