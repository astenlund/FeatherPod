using System.Diagnostics;
using System.Runtime.Versioning;
using FeatherPod.Infrastructure;
using FeatherPod.Shared.Models;
using FeatherPod.Settings.Episode;
using Spectre.Console;
using Spectre.Console.Cli;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Commands.Episode;

internal sealed class PushCommand : AsyncCommand<PushSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, PushSettings settings, CancellationToken cancellationToken)
    {
        if (settings.Headless)
        {
            if (!OperatingSystem.IsWindows())
            {
                Out.Error("Headless mode is only supported on Windows.");

                return 1;
            }

            return await ExecuteHeadlessAsync(settings);
        }

        Out.BlankLine();
        Out.MarkupLine("[bold]FeatherPod Episode Upload[/]");
        Out.BlankLine();

        var env = EnvironmentHelpers.GetEnvironment(settings.Environment);
        if (env is null)
        {
            return 1;
        }

        var (httpClient, currentUser) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient is null)
        {
            return 1;
        }

        var configuration = EnvironmentHelpers.BuildConfiguration(env);

        // Select feed (use -f flag if provided, otherwise prompt user to select)
        FeedConfig? feed;
        if (!string.IsNullOrEmpty(settings.FeedId))
        {
            feed = await FeedHelpers.GetFeedByIdAsync(httpClient, settings.FeedId);
            if (feed == null)
            {
                Out.Error($"Feed '{Markup.Escape(settings.FeedId)}' not found.");
                return 1;
            }
        }
        else
        {
            feed = await FeedHelpers.SelectFeedAsync(httpClient, currentUser: currentUser);
            if (feed == null)
            {
                Out.Error("No feeds available. Create a feed first.");
                return 1;
            }
        }

        // Verify feed access before expensive operations (normalization can take minutes)
        if (currentUser != null && !currentUser.CanAccessFeed(feed.Id))
        {
            Out.Error($"You don't have permission to upload to feed [cyan]{Markup.Escape(feed.Id)}[/].");

            return 1;
        }

        // Expand file patterns (wildcards and comma-separated lists)
        var files = EpisodeHelpers.ExpandFilePatterns(settings.Files);

        if (files.Count == 0)
        {
            Out.Error($"No files found matching pattern: {Markup.Escape(settings.Files)}");
            return 1;
        }

        // Validate that title/description aren't used with multiple files
        if (files.Count > 1)
        {
            if (!string.IsNullOrEmpty(settings.Title))
            {
                Out.Error("Cannot use -t/--title with multiple files (all episodes would get the same title)");
                return 1;
            }

            if (!string.IsNullOrEmpty(settings.Description))
            {
                Out.Error("Cannot use -d/--description with multiple files (all episodes would get the same description)");
                return 1;
            }

            if (!string.IsNullOrEmpty(settings.PublishedDate))
            {
                Out.BlankLine();
                Out.Warning("Using -p/--published-date with multiple files will set the same date for all episodes.");
                var continueAnyway = new MenuBuilder<bool?>()
                    .WithTitle("Continue anyway?")
                    .WithHint("(arrow keys or Y/N)")
                    .AddOption("Y", "Yes", true)
                    .AddOption("N", "No", false)
                    .AllowCancel(true, false)
                    .Show();

                if (continueAnyway != true)
                {
                    Out.Cancelled();
                    return 1;
                }
                Out.BlankLine();
            }
        }

        Out.MarkupLine($"Found [bold]{files.Count}[/] file(s) to upload");
        Out.BlankLine();

        // Dry-run mode: show what would happen and exit (no confirmation needed)
        if (settings.DryRun)
        {
            Out.MarkupLine("[bold]Dry run[/] - no files will be uploaded or deleted");
            Out.BlankLine();

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                Out.MarkupLine($"  Would upload: [cyan]{Markup.Escape(Path.GetFileName(file))}[/] ({EpisodeHelpers.FormatFileSize(fileInfo.Length)})");
            }

            if (settings.DeleteAfter)
            {
                var useTrash = PreferencesHelpers.GetDeleteAfterUploadUseTrash(env) ?? true;
                var deleteMethod = useTrash ? "send to trash" : "permanently delete";
                Out.BlankLine();
                Out.MarkupLine($"  Delete method: [bold]{deleteMethod}[/]");
                foreach (var file in files)
                {
                    Out.MarkupLine($"  Would delete: [cyan]{Markup.Escape(Path.GetFileName(file))}[/]");
                }
            }

            Out.BlankLine().Flush();
            return 0;
        }

        // Confirm upload
        var fileList = files.Count <= 5
            ? string.Join(", ", files.Select(f => $"[cyan]{Markup.Escape(Path.GetFileName(f))}[/]"))
            : $"[bold]{files.Count}[/] files";

        var confirmed = new MenuBuilder<bool?>()
            .WithTitle($"Upload {fileList} to feed [cyan]{Markup.Escape(feed.Title)}[/]?")
            .WithHint("(arrow keys or Y/N, Esc to cancel)")
            .AddOption("Y", "Yes", true)
            .AddOption("N", "No", false)
            .AllowCancel(true, false)
            .Show();

        if (confirmed != true)
        {
            Out.Cancelled();

            return 1;
        }

        Out.BlankLine();

        // Prompt for date source if neither -p nor -x was provided
        var effectiveSettings = settings;
        if (string.IsNullOrEmpty(settings.PublishedDate) && settings.ExtractDateFromFile == null)
        {
            var dateSource = new MenuBuilder<bool?>()
                .WithTitle("Published date source:")
                .WithHint("(arrow keys or highlighted letter, Esc to cancel)")
                .AddOption("C", "Current date/time", false)
                .AddOption("F", "Extract from file metadata", true)
                .AllowCancel()
                .Show();

            if (dateSource == null)
            {
                Out.Cancelled();
                return 1;
            }

            effectiveSettings = new()
            {
                Files = settings.Files,
                Environment = settings.Environment,
                FeedId = settings.FeedId,
                Title = settings.Title,
                Description = settings.Description,
                Summary = settings.Summary,
                PublishedDate = settings.PublishedDate,
                ExtractDateFromFile = dateSource,
                ServerNormalize = settings.ServerNormalize,
                DeleteAfter = settings.DeleteAfter,
                DryRun = settings.DryRun
            };
        }

        var successCount = 0;
        var failureCount = 0;
        var deletedCount = 0;
        var deleteFailedCount = 0;

        foreach (var file in files)
        {
            var result = await EpisodeHelpers.UploadEpisodeAsync(httpClient, configuration, env, feed, file, effectiveSettings, currentUser);
            if (result.Success)
            {
                successCount++;

                // Delete source file after successful upload
                if (effectiveSettings.DeleteAfter && result.EpisodeId != null)
                {
                    var (deleted, failed) = await EpisodeHelpers.TryDeleteSourceAfterUploadAsync(httpClient, feed.Id, result.EpisodeId, file, env);
                    deletedCount += deleted;
                    deleteFailedCount += failed;
                }
            }
            else
            {
                failureCount++;
            }

            Out.BlankLine();
        }

        // Summary
        if (successCount > 0)
        {
            Out.Success($"Successfully uploaded: {successCount}");
        }

        if (deletedCount > 0)
        {
            Out.Success($"Source files deleted: {deletedCount}");
        }

        if (failureCount > 0)
        {
            Out.Error($"Failed: {failureCount}");
        }

        if (deleteFailedCount > 0)
        {
            Out.Warning($"Source file deletions failed: {deleteFailedCount}");
        }

        Out.BlankLine().Flush();

        return failureCount == 0 ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> ExecuteHeadlessAsync(PushSettings settings)
    {
        // Headless mode: no console output -- all errors shown via MessageBox.
        // Launched by featherpod-bridge.exe with CREATE_NO_WINDOW.

        var filePath = settings.Files;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            HeadlessErrorHandler.ShowError($"File not found: {filePath}");

            return 1;
        }

        if (string.IsNullOrEmpty(settings.FeedId))
        {
            HeadlessErrorHandler.ShowError("Feed not specified. Run 'featherpod config context-menu install' to set up.");

            return 1;
        }

        var environment = settings.Environment ?? "Prod";
        var apiKey = PreferencesHelpers.GetApiKey(environment);
        if (string.IsNullOrEmpty(apiKey))
        {
            HeadlessErrorHandler.ShowError($"No API key configured for {environment}. Run 'featherpod config context-menu install' to set up.");

            return 1;
        }

        var configuration = EnvironmentHelpers.BuildConfiguration(environment);
        var serverBaseUrl = configuration["Api:BaseUrl"]
            ?? throw new InvalidOperationException("Api:BaseUrl not configured in appsettings.json");

        using var coordinator = new SingleInstanceCoordinator(settings.FeedId);

        if (coordinator.TryBecomeHost(out var existingHost))
        {
            return await RunAsHostAsync(settings.FeedId, filePath, apiKey, serverBaseUrl, coordinator, settings.DeleteAfter, environment);
        }

        if (existingHost is not null)
        {
            await PostFileToExistingServerAsync(existingHost, filePath);
        }

        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunAsHostAsync(string feedId, string filePath, string apiKey, string serverBaseUrl, SingleInstanceCoordinator coordinator, bool deleteAfter, string environment)
    {
        var server = new LocalFileServer(serverBaseUrl);
        var idleCompletionSource = new TaskCompletionSource();

        server.OnIdleTimeout += () => idleCompletionSource.TrySetResult();

        try
        {
            server.Start();
            coordinator.WriteLockFile(server.Port, server.Token);
            server.AddFile(filePath);

            var pushUrl = $"{serverBaseUrl}/{feedId}/push#{apiKey}&source=localhost:{server.Port}&token={server.Token}";
            Process.Start(new ProcessStartInfo(pushUrl) { UseShellExecute = true });

            await idleCompletionSource.Task;

            if (deleteAfter)
            {
                var useTrash = PreferencesHelpers.GetDeleteAfterUploadUseTrash(environment) ?? true;
                foreach (var path in server.GetUploadedFilePaths())
                {
                    FileTrashService.TryDeleteFile(path, useTrash);
                }
            }
        }
        finally
        {
            coordinator.DeleteLockFile();
            server.Dispose();
        }

        return 0;
    }

    private static async Task PostFileToExistingServerAsync(LockFileInfo host, string filePath)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = System.Text.Json.JsonSerializer.Serialize(new { path = filePath });
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync($"http://127.0.0.1:{host.Port}/api/files?token={host.Token}", content);
        }
        catch
        {
            // Existing server may have shut down between validation and POST -- silently ignore.
        }
    }
}
