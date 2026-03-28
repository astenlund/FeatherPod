using System.Net.Http.Headers;
using System.Text.Json;
using Spectre.Console;

using static FeatherPod.Infrastructure.ConsoleWriter;

namespace FeatherPod.Infrastructure;

internal static class YouTubeHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static async Task<bool> UploadCookiesAsync(HttpClient httpClient, string cookiePath)
    {
        try
        {
            const string url = "/api/youtube/cookies";
            Out.MarkupLine($"[grey]Uploading cookies to: {Markup.Escape(url)}[/]");

            await using var fileStream = File.OpenRead(cookiePath);
            using var formData = new MultipartFormDataContent();
            using var fileContent = new StreamContent(fileStream);

            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            formData.Add(fileContent, "file", Path.GetFileName(cookiePath));

            var response = await httpClient.PostAsync(url, formData);

            Out.MarkupLine($"[grey]Response status: {response.StatusCode}[/]");

            if (response.IsSuccessStatusCode)
            {
                Out.BlankLine();
                Out.Success("YouTube cookies uploaded successfully");

                return true;
            }

            var errorContent = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Out.Error("Permission denied. Only admins can upload YouTube cookies.");
            }
            else
            {
                var errorMessage = TryParseErrorMessage(errorContent) ?? $"{response.StatusCode}";
                Out.Error($"Failed to upload cookies: {Markup.Escape(errorMessage)}");
            }

            return false;
        }
        catch (Exception ex)
        {
            Out.Error($"Error uploading cookies: {Markup.Escape(ex.Message)}");

            return false;
        }
    }

    internal static async Task<CookieStatusResult?> GetCookieStatusAsync(HttpClient httpClient)
    {
        try
        {
            var response = await httpClient.GetAsync("/api/youtube/cookies/status");

            if (!response.IsSuccessStatusCode)
            {
                Out.Error($"Failed to get cookie status: {response.StatusCode}");

                return null;
            }

            var json = await response.Content.ReadAsStringAsync();

            return JsonSerializer.Deserialize<CookieStatusResult>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Out.Error($"Error checking cookie status: {Markup.Escape(ex.Message)}");

            return null;
        }
    }

    internal static void DisplayCookieStatus(CookieStatusResult status)
    {
        if (status.HasCookies)
        {
            Out.MarkupLine("YouTube Cookies: [green]Uploaded[/]");
            Out.MarkupLine($"  Uploaded at: [cyan]{Markup.Escape(status.UploadedAt?.ToString("yyyy-MM-dd HH:mm UTC") ?? "Unknown")}[/]");
            Out.MarkupLine($"  Uploaded by: [cyan]{Markup.Escape(status.UploadedBy ?? "Unknown")}[/]");
            if (status.FileSize.HasValue)
            {
                var sizeKb = status.FileSize.Value / 1024.0;
                Out.MarkupLine($"  File size: [cyan]{sizeKb:F1} kB[/]");
            }
        }
        else
        {
            Out.MarkupLine("YouTube Cookies: [yellow]Not uploaded[/]");
        }
    }

    private static string? TryParseErrorMessage(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
            {
                return errorProp.GetString();
            }
        }
        catch
        {
            // Not JSON or no error property
        }

        return null;
    }
}

internal record CookieStatusResult
{
    public bool HasCookies { get; init; }
    public DateTimeOffset? UploadedAt { get; init; }
    public string? UploadedBy { get; init; }
    public long? FileSize { get; init; }
}
