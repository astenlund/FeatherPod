using System.Reflection;
using System.Text.Json;
using FeatherPod.Infrastructure;
using FeatherPod.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FeatherPod.Commands;

internal sealed class VersionCommand : AsyncCommand<VersionSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, VersionSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]FeatherPod Version[/]");
        AnsiConsole.WriteLine();

        // Show CLI version
        var versionAttribute = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var cliVersion = versionAttribute?.InformationalVersion ?? "unknown";
        AnsiConsole.MarkupLine($"[cyan]CLI Version:[/] {cliVersion}");
        AnsiConsole.WriteLine();

        // Always show server version (default to Prod like other commands)
        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env == null)
        {
            return 1;
        }

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null)
        {
            return 1;
        }

        try
        {
            var response = await httpClient.GetAsync("/api/version", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var versionInfo = JsonSerializer.Deserialize<JsonElement>(json);

                if (versionInfo.TryGetProperty("version", out var version))
                {
                    AnsiConsole.MarkupLine($"[cyan]Server Version:[/] {version.GetString()}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]Could not fetch server version: {response.StatusCode}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Could not fetch server version: {ex.Message}[/]");
        }

        AnsiConsole.WriteLine();

        return 0;
    }
}
