using FeatherPod.Infrastructure;
using FeatherPod.Settings.YouTube;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.YouTube;

internal sealed class SetCookiesCommand : AsyncCommand<SetCookiesSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SetCookiesSettings settings, CancellationToken cancellationToken)
    {
        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod YouTube Set Cookies[/]");
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

        var cookiePath = settings.CookieFilePath.Trim().Trim('"', '\'');
        if (!File.Exists(cookiePath))
        {
            Out.Error($"Cookie file not found: {Markup.Escape(cookiePath)}");

            return 1;
        }

        var success = await YouTubeHelpers.UploadCookiesAsync(httpClient, cookiePath);

        Out.BlankLine().Flush();

        return success ? 0 : 1;
    }
}
