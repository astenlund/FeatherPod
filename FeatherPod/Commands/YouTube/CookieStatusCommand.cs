using FeatherPod.Infrastructure;
using FeatherPod.Settings.YouTube;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.YouTube;

internal sealed class CookieStatusCommand : AsyncCommand<CookieStatusSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CookieStatusSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod YouTube Cookie Status[/]");
        Out.BlankLine();

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

        var status = await YouTubeHelpers.GetCookieStatusAsync(httpClient);
        if (status == null)
        {
            return 1;
        }

        YouTubeHelpers.DisplayCookieStatus(status);

        Out.BlankLine().Flush();

        return 0;
    }
}
