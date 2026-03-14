using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using FeatherPod.Shared.Models;
using Spectre.Console;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Infrastructure;

internal static class FeedHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// Resolves environment, sets up HTTP client, and fetches a feed by ID (prompting if not provided).
    /// Returns null values on failure (errors are already printed).
    /// </summary>
    internal static async Task<(HttpClient? HttpClient, FeedConfig? Feed)> ResolveEnvironmentAndFeedAsync(string? environmentName, string? feedId)
    {
        var env = EnvironmentHelpers.GetEnvironment(environmentName);
        if (env == null)
        {
            return (null, null);
        }

        var (httpClient, _) = await EnvironmentHelpers.SetupHttpClientAsync(env);
        if (httpClient == null)
        {
            return (null, null);
        }

        var resolvedFeedId = feedId?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedFeedId))
        {
            var selectedFeed = await SelectFeedAsync(httpClient);
            if (selectedFeed == null)
            {
                Out.Error("No feeds available.");

                return (httpClient, null);
            }
            resolvedFeedId = selectedFeed.Id;
        }

        var feed = await GetFeedByIdAsync(httpClient, resolvedFeedId);
        if (feed == null)
        {
            Out.Error($"Feed '{resolvedFeedId}' not found.");

            return (httpClient, null);
        }

        return (httpClient, feed);
    }

    internal static async Task<List<FeedConfig>> GetFeedsAsync(HttpClient httpClient)
    {
        try
        {
            var response = await httpClient.GetAsync("/api/feeds");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<FeedConfig>>(json, JsonOptions) ?? [];
        }
        catch (HttpRequestException ex)
        {
            Out.Error($"Error fetching feeds: {ex.Message}");

            return [];
        }
    }

    internal static async Task<FeedConfig?> GetFeedByIdAsync(HttpClient httpClient, string feedId)
    {
        try
        {
            var response = await httpClient.GetAsync($"/api/feeds/{feedId}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<FeedConfig>(json, JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            Out.Error($"Error fetching feed: {ex.Message}");

            return null;
        }
    }

    internal static async Task<FeedConfig?> SelectFeedAsync(HttpClient httpClient, string? contextMessage = null, CurrentUserInfo? currentUser = null, bool showNoFeedsMessage = true)
    {
        var feeds = await GetFeedsAsync(httpClient);

        if (currentUser?.Role == "FeedOwner")
        {
            feeds = feeds.Where(f => currentUser.OwnedFeeds.Contains(f.Id)).ToList();
        }

        if (feeds.Count == 0)
        {
            if (showNoFeedsMessage)
            {
                var message = currentUser?.Role == "FeedOwner"
                    ? "[yellow]No feeds available.[/] You don't own any feeds."
                    : "[yellow]No feeds found.[/] Use [cyan]'M: Manage Feeds'[/] to create one.";
                Out.MarkupLine(message);
                Out.BlankLine();
            }

            return null;
        }

        if (feeds.Count == 1)
        {
            return feeds[0];
        }

        var menu = new MenuBuilder<FeedConfig?>()
            .WithTitle($"{contextMessage} Select feed:".Trim())
            .WithHint("(arrow keys, Enter to select)")
            .AllowCancel();

        foreach (var feed in feeds)
        {
            menu.AddOption(
                null,
                $"[cyan]{Markup.Escape(feed.Title)}[/]",
                feed
            );
        }

        return menu.Show();
    }

    internal static async Task<bool> UploadIconAsync(HttpClient httpClient, string feedId, string iconPath)
    {
        try
        {
            var url = $"/api/feeds/{feedId}/icon";
            Out.MarkupLine($"[grey]Uploading icon to: {Markup.Escape(url)}[/]");
            Out.MarkupLine($"[grey]Base URL: {Markup.Escape(httpClient.BaseAddress?.ToString() ?? "null")}[/]");

            await using var fileStream = File.OpenRead(iconPath);
            using var formData = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);

            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            formData.Add(fileContent, "file", Path.GetFileName(iconPath));

            var response = await httpClient.PostAsync(url, formData);

            Out.MarkupLine($"[grey]Response status: {response.StatusCode}[/]");

            if (response.IsSuccessStatusCode)
            {
                Out.Success("Uploaded icon");

                return true;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Out.Error($"Failed to upload icon: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    Out.Error(errorContent);
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Out.Error($"Error uploading icon: {ex.Message}");

            return false;
        }
    }

    internal static async Task<(bool Success, FeedConfig? UpdatedFeed, string? Error)> UpdateFeedConfigAsync(
        HttpClient httpClient,
        FeedConfig currentFeed,
        bool? useFileMetadataForPublishDate = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Build complete FeedConfig with updated values
            var updatedFeed = currentFeed with
            {
                UseFileMetadataForPublishDate = useFileMetadataForPublishDate ?? currentFeed.UseFileMetadataForPublishDate
            };

            var json = JsonSerializer.Serialize(updatedFeed);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync($"/api/feeds/{currentFeed.Id}", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var refreshedFeed = await GetFeedByIdAsync(httpClient, currentFeed.Id);

                return (true, refreshedFeed, null);
            }

            var errorContent = "" + await response.Content.ReadAsStringAsync(cancellationToken);

            return (false, null, $"Failed to update feed configuration: {response.StatusCode}\n{errorContent}".Trim());
        }
        catch (Exception ex)
        {
            return (false, null, $"Error updating feed configuration: {ex.Message}");
        }
    }

    internal static async Task<bool> DeleteIconAsync(HttpClient httpClient, string feedId)
    {
        try
        {
            var url = $"/api/feeds/{feedId}/icon";
            var response = await httpClient.DeleteAsync(url);

            if (response.IsSuccessStatusCode)
            {
                Out.Success("Removed icon");

                return true;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Out.Warning(errorContent);

                return false;
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Out.Error($"Failed to delete icon: {response.StatusCode}");
                if (!string.IsNullOrEmpty(errorContent))
                {
                    Out.Error(errorContent);
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            Out.Error($"Error deleting icon: {ex.Message}");

            return false;
        }
    }

}
