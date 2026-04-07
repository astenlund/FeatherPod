using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherPod.Shared;
using FeatherPod.Shared.Models;
using FeatherPod.Settings.Episode;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Infrastructure;

internal static class EpisodeHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    internal record UploadResult(bool Success, string? EpisodeId = null);

    internal static List<string> ExpandFilePatterns(string input)
    {
        var result = new List<string>();

        // Split by comma for comma-separated lists
        var patterns = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pattern in patterns)
        {
            // Check if pattern contains wildcards
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                // Get directory and search pattern
                var directory = Path.GetDirectoryName(pattern);
                var searchPattern = Path.GetFileName(pattern);

                // If no directory specified, use current directory
                if (string.IsNullOrEmpty(directory))
                {
                    directory = Directory.GetCurrentDirectory();
                }

                if (Directory.Exists(directory))
                {
                    var matchingFiles = Directory.GetFiles(directory, searchPattern);
                    result.AddRange(matchingFiles);
                }
            }
            else
            {
                // Regular file path
                if (File.Exists(pattern))
                {
                    result.Add(pattern);
                }
            }
        }

        return result;
    }

    internal static async Task<UploadResult> UploadEpisodeAsync(HttpClient httpClient, IConfiguration configuration, string environment, FeedConfig feed, string filePath, PushSettings settings, CurrentUserInfo? currentUser = null, bool? normalizationOverride = null)
    {
        var fileName = Path.GetFileName(filePath);
        string? uploadedEpisodeId = null;
        string? normalizedTempFile = null;

        // Safety net: verify feed access before expensive normalization
        if (currentUser != null && !currentUser.CanAccessFeed(feed.Id))
        {
            Out.Error($"You don't have permission to upload to feed [cyan]{Markup.Escape(feed.Id)}[/].");

            return new UploadResult(false);
        }

        // Generate episode ID from ORIGINAL file size (before any normalization)
        var originalFileSize = new FileInfo(filePath).Length;
        var episodeId = Episode.GenerateId(feed.Id, fileName, originalFileSize);

        // Extract creation time from ORIGINAL file if requested (before normalization)
        string? extractedPublishedDate = null;
        if (settings.ExtractDateFromFile == true)
        {
            var creationTime = FFmpegService.ExtractCreationTime(filePath);
            if (creationTime.HasValue)
            {
                extractedPublishedDate = creationTime.Value.ToString("o"); // ISO 8601 format
            }
        }

        try
        {
            // Load audio normalization configuration from appsettings (for LUFS target, etc.)
            var normalizationConfig = new AudioNormalizationConfig();
            configuration.GetSection("AudioNormalization").Bind(normalizationConfig);

            // Use explicit override if provided, otherwise read from user preferences
            normalizationConfig.Enabled = normalizationOverride
                ?? PreferencesHelpers.GetNormalizationEnabled(environment)
                ?? true;

            // Check if normalization is enabled (skip client-side if server-side requested)
            var fileToUpload = filePath;
            if (settings.ServerNormalize)
            {
                Out.MarkupLine($"[cyan]{Markup.Escape(fileName)}[/] will be normalized on server to -16 LUFS");
            }
            else if (normalizationConfig.Enabled)
            {
                // Check if FFmpeg is available
                if (!FFmpegService.IsFFmpegAvailable())
                {
                    Out.BlankLine();
                    Out.Warning("FFmpeg is not installed or not found.");

                    // Offer to download FFmpeg automatically
                    var downloadChoice = new MenuBuilder<bool?>()
                        .WithTitle("Download FFmpeg automatically?")
                        .WithHint("(arrow keys or Y/N, Esc to cancel)")
                        .AddOption("Y", "Yes - Download FFmpeg (~100MB)", true)
                        .AddOption("N", "No - Skip normalization", false)
                        .AllowCancel(true, false)
                        .Show();

                    if (downloadChoice == true)
                    {
                        Out.BlankLine();
                        var downloadSuccess = await DownloadFFmpegWithProgressAsync();

                        if (!downloadSuccess)
                        {
                            Out.Error("Failed to download FFmpeg.");
                            Out.BlankLine();

                            var continueAfterFailure = new MenuBuilder<bool?>()
                                .WithTitle("Continue upload without normalization?")
                                .WithHint("(arrow keys or Y/N, Esc to cancel)")
                                .AddOption("Y", "Yes - Upload original file", true)
                                .AddOption("N", "No - Cancel upload", false)
                                .AllowCancel(true, false)
                                .Show();

                            if (continueAfterFailure != true)
                            {
                                Out.Cancelled();

                                return new UploadResult(false);
                            }
                        }
                        else
                        {
                            Out.Success("FFmpeg downloaded successfully");
                        }

                        Out.BlankLine();
                    }
                    else if (downloadChoice == false)
                    {
                        // User chose not to download, continue without normalization
                        Out.BlankLine();
                    }
                    else
                    {
                        // User cancelled (Esc)
                        Out.Cancelled();

                        return new UploadResult(false);
                    }
                }

                // Re-check availability after potential download
                if (FFmpegService.IsFFmpegAvailable())
                {
                    // Normalize audio using shared progress renderer
                    var normResult = await NormalizationProgressRenderer.RunWithProgressAsync(
                        fileName,
                        async (updateProgress, _) =>
                        {
                            normalizedTempFile = await FFmpegService.NormalizeAudioAsync(filePath, normalizationConfig, updateProgress);

                            return new(normalizedTempFile != null);
                        },
                        CancellationToken.None);

                    if (!normResult.Success)
                    {
                        Out.BlankLine();
                        Out.Warning("Audio normalization failed.");

                        var continueWithOriginal = new MenuBuilder<bool?>()
                            .WithTitle("Continue upload with original (unnormalized) file?")
                            .WithHint("(arrow keys or Y/N, Esc to cancel)")
                            .AddOption("Y", "Yes - Upload original file", true)
                            .AddOption("N", "No - Cancel upload", false)
                            .AllowCancel(true, false)
                            .Show();

                        if (continueWithOriginal != true)
                        {
                            Out.Cancelled();

                            return new UploadResult(false);
                        }
                    }
                    else
                    {
                        fileToUpload = normalizedTempFile!;
                        NormalizationProgressRenderer.DisplayResult(normResult);
                    }

                    Out.BlankLine();
                }
            }

            // Track job for async normalization polling (must happen outside Status block)
            string? pendingJobId = null;

            await AnsiConsole.Status()
                .StartAsync($"Uploading [cyan]{Markup.Escape(fileName)}[/]...", async _ =>
                {
                    try
                    {
                        // Create multipart form data
                        using var content = new MultipartFormDataContent();

                        // Add file (use normalized file if available, otherwise original)
                        var fileBytes = await File.ReadAllBytesAsync(fileToUpload);
                        var fileContent = new ByteArrayContent(fileBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue(AudioHelper.GetMimeType(fileName));
                        content.Add(fileContent, "file", fileName);

                        // Add optional title
                        if (!string.IsNullOrEmpty(settings.Title))
                        {
                            content.Add(new StringContent(settings.Title), "title");
                        }

                        // Add optional description
                        if (!string.IsNullOrEmpty(settings.Description))
                        {
                            content.Add(new StringContent(settings.Description), "description");
                        }

                        // Add optional summary
                        if (!string.IsNullOrEmpty(settings.Summary))
                        {
                            content.Add(new StringContent(settings.Summary), "summary");
                        }

                        // Add published date options
                        if (!string.IsNullOrEmpty(settings.PublishedDate))
                        {
                            content.Add(new StringContent(settings.PublishedDate), "publishedDate");
                        }
                        else if (!string.IsNullOrEmpty(extractedPublishedDate))
                        {
                            // Use extracted date from original file (before normalization)
                            content.Add(new StringContent(extractedPublishedDate), "publishedDate");
                        }
                        // Note: UseCurrentDate is the default behavior, no need to send anything

                        // Add episode ID (based on original file size, before any normalization)
                        content.Add(new StringContent(episodeId), "episodeId");

                        // Upload (add source and normalize query params)
                        var uploadUrl = $"/api/feeds/{feed.Id}/episodes?source=CLI";
                        if (settings.ServerNormalize)
                        {
                            uploadUrl += "&normalize=true";
                        }
                        var response = await httpClient.PostAsync(uploadUrl, content);

                        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
                        {
                            // Async normalization - store job ID for polling outside this block
                            var responseContent = await response.Content.ReadAsStringAsync();
                            var jobStatus = JsonSerializer.Deserialize<JobStatusResponse>(responseContent, JsonOptions);
                            pendingJobId = jobStatus?.JobId;
                        }
                        else if (response.IsSuccessStatusCode)
                        {
                            uploadedEpisodeId = episodeId;
                            var responseContent = await response.Content.ReadAsStringAsync();
                            var episode = JsonSerializer.Deserialize<Episode>(responseContent, JsonOptions);

                            Out.Success($"Uploaded: [cyan]{Markup.Escape(fileName)}[/]");
                            Out.BlankLine();

                            if (episode != null)
                            {
                                DisplayEpisodeDetails(episode);
                            }
                        }
                        else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            Out.Error("Unauthorized: Check your API key configuration");
                        }
                        else
                        {
                            var errorContent = await response.Content.ReadAsStringAsync();
                            Out.Error($"Failed to upload [cyan]{Markup.Escape(fileName)}[/]: {response.StatusCode}");
                            if (!string.IsNullOrEmpty(errorContent))
                            {
                                Out.Error(Markup.Escape(errorContent), indent: 2);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Out.Error($"Error uploading [cyan]{Markup.Escape(fileName)}[/]: {Markup.Escape(ex.Message)}");
                    }
                });

            // Poll for async normalization completion (outside Status block to avoid nesting)
            if (pendingJobId != null)
            {
                Out.Success($"Uploaded: [cyan]{Markup.Escape(fileName)}[/] (normalizing on server...)");
                var jobSuccess = await PollJobCompletionAsync(httpClient, pendingJobId, fileName);
                if (jobSuccess)
                {
                    uploadedEpisodeId = episodeId;
                }
            }

            return new UploadResult(uploadedEpisodeId != null, uploadedEpisodeId);
        }
        finally
        {
            FileHelper.TryDeleteFile(normalizedTempFile, NullLogger.Instance);
        }
    }

    internal static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "kB", "MB", "GB"];
        double len = bytes;
        var order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
            : $"{duration.Minutes}:{duration.Seconds:D2}";
    }

    internal static async Task<List<Episode>?> GetEpisodesAsync(HttpClient httpClient, string feedId)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/feeds/{feedId}/episodes");

            if (!response.IsSuccessStatusCode)
            {
                Out.Error($"Failed to fetch episodes from feed '{Markup.Escape(feedId)}'");

                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var episodes = JsonSerializer.Deserialize<List<Episode>>(json, JsonOptions);
            return episodes;
        }
        catch (Exception ex)
        {
            Out.Error($"Error fetching episodes: {Markup.Escape(ex.Message)}");

            return null;
        }
    }

    internal static async Task<bool> VerifyEpisodeExistsAsync(HttpClient httpClient, string feedId, string episodeId)
    {
        try
        {
            var episodes = await GetEpisodesAsync(httpClient, feedId);
            return episodes?.Any(e => e.Id == episodeId) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies the episode exists on the server, then deletes the source file.
    /// Returns (deleted: true/false, failed: true/false) for summary counting.
    /// </summary>
    internal static async Task<(int Deleted, int Failed)> TryDeleteSourceAfterUploadAsync(
        HttpClient httpClient, string feedId, string episodeId, string filePath, string environment)
    {
        var verified = await VerifyEpisodeExistsAsync(httpClient, feedId, episodeId);
        if (!verified)
        {
            Out.Warning("Skipped source deletion - could not verify episode on server");
            return (0, 1);
        }

        var useTrash = PreferencesHelpers.GetDeleteAfterUploadUseTrash(environment) ?? true;
        var deleteResult = FileTrashService.TryDeleteFile(filePath, useTrash);
        if (deleteResult.Success)
        {
            Out.MarkupLine($"  Source file {deleteResult.Method}: [cyan]{Markup.Escape(Path.GetFileName(filePath))}[/]");
            return (1, 0);
        }

        Out.Warning($"Could not delete source file: {Markup.Escape(deleteResult.Error ?? "unknown error")}");
        return (0, 1);
    }

    internal static async Task<bool> MoveEpisodeAsync(HttpClient httpClient, string fromFeed, string episodeId, string toFeed)
    {
        try
        {
            var requestBody = new { targetFeedId = toFeed };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"/api/feeds/{fromFeed}/episodes/{episodeId}/move", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorJson = await response.Content.ReadAsStringAsync();
                var errorObj = JsonSerializer.Deserialize<JsonElement>(errorJson);
                var errorMsg = errorObj.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
                Out.Error(Markup.Escape(errorMsg ?? "Unknown error"));

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Out.Error($"Error moving episode: {Markup.Escape(ex.Message)}");

            return false;
        }
    }

    internal static async Task<bool> CopyEpisodeAsync(HttpClient httpClient, string fromFeed, string episodeId, string toFeed)
    {
        try
        {
            var requestBody = new { targetFeedId = toFeed };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"/api/feeds/{fromFeed}/episodes/{episodeId}/copy", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorJson = await response.Content.ReadAsStringAsync();
                var errorObj = JsonSerializer.Deserialize<JsonElement>(errorJson);
                var errorMsg = errorObj.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
                Out.Error(Markup.Escape(errorMsg ?? "Unknown error"));

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Out.Error($"Error copying episode: {Markup.Escape(ex.Message)}");

            return false;
        }
    }

    internal static async Task<EpisodeOperationResult> UpdateEpisodeTitleAsync(
        HttpClient httpClient, string feedId, string episodeId, string newTitle,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var requestBody = new { title = newTitle };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PatchAsync($"/api/feeds/{feedId}/episodes/{episodeId}", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return new() { Success = true, EpisodeId = episodeId };
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Out.Error("Episode not found.");

                return new() { Success = false, ErrorMessage = "Episode not found" };
            }

            var errorJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorObj = JsonSerializer.Deserialize<JsonElement>(errorJson);
            var errorMsg = errorObj.TryGetProperty("error", out var err) ? err.GetString() : "Unknown error";
            Out.Error(Markup.Escape(errorMsg ?? "Unknown error"));

            return new() { Success = false, ErrorMessage = errorMsg };
        }
        catch (Exception ex)
        {
            Out.Error($"Error renaming episode: {Markup.Escape(ex.Message)}");

            return new() { Success = false, ErrorMessage = ex.Message };
        }
    }

    internal static async Task<string?> SuggestTitleAsync(
        HttpClient httpClient, string feedId, string episodeId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsync(
                $"/api/feeds/{feedId}/episodes/{episodeId}/suggest-title", null, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            return result.TryGetProperty("suggestedTitle", out var title) ? title.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    internal static List<Episode> SelectEpisodesMulti(List<Episode> episodes)
    {
        if (episodes.Count == 0)
        {
            return [];
        }

        var prompt = new MultiSelectionPrompt<Episode>()
            .Title("Select episodes:")
            .PageSize(10)
            .NotRequired()
            .MoreChoicesText("[grey](Move up/down for more)[/]")
            .InstructionsText("[grey]([blue]Space[/] to toggle, [green]Enter[/] to confirm, Enter with none to cancel)[/]")
            .UseConverter(EpisodeConverter(episodes))
            .AddChoices(episodes);

        return AnsiConsole.Prompt(prompt);
    }

    internal static Episode? SelectEpisodeSingle(List<Episode> episodes)
    {
        if (episodes.Count == 0)
        {
            return null;
        }

        var prompt = new SelectionPrompt<Episode>()
            .Title("Select episode:")
            .PageSize(10)
            .MoreChoicesText("[grey](Move up/down for more)[/]")
            .EnableSearch()
            .SearchPlaceholderText("[grey](type to search)[/]")
            .UseConverter(EpisodeConverter(episodes))
            .AddChoices(episodes);

        return AnsiConsole.Prompt(prompt);
    }

    private static Func<Episode, string> EpisodeConverter(List<Episode> episodes)
    {
        var numberById = episodes.Select((ep, i) => (ep.Id, Number: episodes.Count - i))
            .ToDictionary(x => x.Id, x => x.Number);

        return ep =>
        {
            var number = numberById.TryGetValue(ep.Id, out var n) ? n : 0;

            return $"[grey]#{number}[/] {Markup.Escape(ep.Title)}";
        };
    }

    internal static List<Episode> MatchEpisodesByPattern(List<Episode> episodes, string pattern)
    {
        // Try exact ID match first
        var exactMatch = episodes.FirstOrDefault(e => e.Id == pattern);
        if (exactMatch != null)
        {
            return [exactMatch];
        }

        // Wildcard match on filename or title (case-insensitive)
        if (pattern == "*")
        {
            return episodes;
        }

        var lower = pattern.ToLower();

        // Contains pattern: *text*
        if (pattern.StartsWith('*') && pattern.EndsWith('*'))
        {
            var contains = lower.Trim('*');
            return episodes.Where(e =>
                e.FileName.ToLower().Contains(contains) ||
                e.Title.ToLower().Contains(contains)).ToList();
        }

        // Prefix pattern: text*
        if (pattern.EndsWith('*'))
        {
            var prefix = lower[..^1];
            return episodes.Where(e =>
                e.FileName.ToLower().StartsWith(prefix) ||
                e.Title.ToLower().StartsWith(prefix)).ToList();
        }

        // Suffix pattern: *text
        if (pattern.StartsWith('*'))
        {
            var suffix = lower[1..];
            return episodes.Where(e =>
                e.FileName.ToLower().EndsWith(suffix) ||
                e.Title.ToLower().EndsWith(suffix)).ToList();
        }

        // Literal match (no wildcards) - match filename or title
        return episodes.Where(e =>
            e.FileName.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
            e.Title.Equals(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static async Task<bool> DownloadFFmpegWithProgressAsync()
    {
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[bold]Downloading FFmpeg...[/]", async _ => await FFmpegService.DownloadFFmpegAsync());
    }

    private static async Task<bool> PollJobCompletionAsync(HttpClient httpClient, string jobId, string fileName)
    {
        const int maxWaitMs = 600000; // 10 minutes
        using var cts = new CancellationTokenSource(maxWaitMs);

        try
        {
            var result = await NormalizationProgressRenderer.RunWithProgressAsync(
                fileName,
                async (updateProgress, ct) => await StreamSSEProgressAsync(httpClient, jobId, updateProgress, ct),
                cts.Token);

            NormalizationProgressRenderer.DisplayResult(result);

            return result.Success;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Out.Warning("SSE connection failed, falling back to polling...");

            return await FallbackPollJobCompletionAsync(httpClient, jobId, fileName, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Out.BlankLine();
            Out.Warning($"Normalization timed out after {maxWaitMs / 1000} seconds");
            Out.MarkupLine($"  Job ID: [grey]{jobId}[/] - check status manually");

            return false;
        }
    }

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    private static async Task<NormalizationProgressRenderer.NormalizationResult> StreamSSEProgressAsync(
        HttpClient httpClient,
        string jobId,
        Action<ProgressUpdate> updateProgress,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/jobs/{jobId}/progress");
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"SSE endpoint returned {response.StatusCode}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        JobStatusResponse? finalStatus = null;
        string? currentEvent = null;
        var dataBuilder = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(ReadTimeout);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(readCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"SSE read timed out after {ReadTimeout.TotalSeconds} seconds");
            }

            if (line == null)
            {
                break;
            }

            if (line.StartsWith("event:"))
            {
                currentEvent = line[6..].Trim();
            }
            else if (line.StartsWith("data:"))
            {
                dataBuilder.AppendLine(line[5..].Trim());
            }
            else if (line.StartsWith(':'))
            {
                // Comment/heartbeat - ignore
            }
            else if (string.IsNullOrWhiteSpace(line) && currentEvent != null)
            {
                if (currentEvent == "progress")
                {
                    var data = dataBuilder.ToString().Trim();
                    var status = JsonSerializer.Deserialize<JobStatusResponse>(data, JsonOptions)!;
                    finalStatus = status;

                    var stage = Enum.TryParse<NormalizationStage>(status.Stage, out var s)
                        ? s
                        : NormalizationStage.Unknown;

                    updateProgress(new()
                    {
                        Stage = stage,
                        ProgressPercent = status.ProgressPercent ?? 0,
                        Message = status.ProgressMessage ?? "",
                        CurrentPosition = status.CurrentPositionMs.HasValue
                            ? TimeSpan.FromMilliseconds(status.CurrentPositionMs.Value)
                            : null,
                        TotalDuration = status.TotalDurationMs.HasValue
                            ? TimeSpan.FromMilliseconds(status.TotalDurationMs.Value)
                            : null,
                        StageDisplayName = status.StageDisplayName,
                        StageDisplayNameMaxLength = status.StageDisplayNameMaxLength
                    });
                }
                else if (currentEvent is "done" or "error")
                {
                    break;
                }

                dataBuilder.Clear();
                currentEvent = null;
            }
        }

        if (finalStatus?.Status == nameof(JobStatus.Completed))
        {
            return new(true, EpisodeId: finalStatus.EpisodeId);
        }

        if (finalStatus?.Status == nameof(JobStatus.Failed))
        {
            return new(false, Error: finalStatus.Error ?? "Unknown error");
        }

        if (finalStatus?.Status == nameof(JobStatus.Cancelled))
        {
            return new(false, Error: "Job was cancelled");
        }

        return new(false, Error: "Connection closed unexpectedly");
    }

    private static async Task<bool> FallbackPollJobCompletionAsync(HttpClient httpClient, string jobId, string fileName, CancellationToken cancellationToken)
    {
        const int pollIntervalMs = 2000;
        var elapsed = 0;

        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"[bold]Normalizing {Markup.Escape(fileName)}...[/]", async ctx =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(pollIntervalMs, cancellationToken);
                    elapsed += pollIntervalMs;

                    try
                    {
                        var response = await httpClient.GetAsync($"/api/jobs/{jobId}", cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            Out.BlankLine();
                            Out.Error($"Failed to check job status: {response.StatusCode}");

                            return false;
                        }

                        var json = await response.Content.ReadAsStringAsync(cancellationToken);
                        var status = JsonSerializer.Deserialize<JobStatusResponse>(json, JsonOptions);

                        if (status == null)
                        {
                            continue;
                        }

                        if (status.Status == nameof(JobStatus.Completed))
                        {
                            Out.BlankLine();
                            Out.Success("Normalization complete");
                            Out.BlankLine();
                            Out.MarkupLine($"  Episode ID: [grey]{status.EpisodeId}[/]");

                            return true;
                        }

                        if (status.Status == nameof(JobStatus.Failed))
                        {
                            Out.BlankLine();
                            Out.Error($"Normalization failed: {Markup.Escape(status.Error ?? "Unknown error")}");

                            return false;
                        }

                        if (status.Status == nameof(JobStatus.Cancelled))
                        {
                            Out.BlankLine();
                            Out.Warning("Job was cancelled");

                            return false;
                        }

                        ctx.Status($"[bold]Normalizing {Markup.Escape(fileName)}...[/] ({elapsed / 1000}s)");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Out.BlankLine();
                        Out.Error($"Error checking job status: {Markup.Escape(ex.Message)}");

                        return false;
                    }
                }

                return false;
            });
    }

    private static void DisplayEpisodeDetails(Episode episode)
    {
        Out.MarkupLine($"  ID: [grey]{episode.Id}[/]");
        Out.MarkupLine($"  Title: {Markup.Escape(episode.Title)}");
        Out.MarkupLine($"  Published: [grey]{episode.PublishedDate:yyyy-MM-dd HH:mm:ss}[/]");
        Out.MarkupLine($"  Duration: [grey]{FormatDuration(episode.Duration)}[/]");
        Out.MarkupLine($"  Size: [grey]{FormatFileSize(episode.FileSize)}[/]");
    }
}
