using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace FeatherPod.Shared.Services;

public interface IAiService
{
    bool IsAvailable { get; }
    Task<string?> SuggestTitleAsync(string filename, string? note = null, CancellationToken cancellationToken = default);
}

public class AiService : IAiService
{
    private readonly ChatClient? _chatClient;
    private readonly ILogger<AiService> _logger;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 1;

    private const string SystemPrompt = """
        You are a podcast episode title generator. Given a filename, produce a clean, readable episode title.

        Rules:
        - Remove file extensions, numbering artifacts, and encoding artifacts
        - Expand abbreviations and fix truncated words where obvious
        - Convert underscores, hyphens, and camelCase into natural spacing
        - Preserve technical terms, proper nouns, and acronyms (e.g., AI, GPU, 3D)
        - Do not add quotes or formatting
        - Return only the title text, nothing else
        - Keep it concise (under 100 characters when possible)
        """;

    public bool IsAvailable => _chatClient != null;

    public AiService(IConfiguration configuration, ILogger<AiService> logger)
    {
        _logger = logger;

        var endpoint = configuration["AzureOpenAI:Endpoint"];
        var deployment = configuration["AzureOpenAI:Deployment"];

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(deployment))
        {
            _logger.LogInformation("AzureOpenAI not configured; AI title suggestions disabled");

            return;
        }

        var client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
        _chatClient = client.GetChatClient(deployment);

        _logger.LogInformation("AI title suggestions enabled (endpoint: {Endpoint}, deployment: {Deployment})", endpoint, deployment);
    }

    public async Task<string?> SuggestTitleAsync(string filename, string? note = null, CancellationToken cancellationToken = default)
    {
        if (_chatClient == null)
        {
            return null;
        }

        var userMessage = string.IsNullOrWhiteSpace(note)
            ? filename
            : $"{filename}\n\nUser note: {note}";

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);

            try
            {
                var completion = await _chatClient.CompleteChatAsync(
                    [
                        new SystemChatMessage(SystemPrompt),
                        new UserChatMessage(userMessage),
                    ],
                    new ChatCompletionOptions { MaxOutputTokenCount = 80, Temperature = 0.3f },
                    timeoutCts.Token);

                var finishReason = completion.Value.FinishReason;
                var content = completion.Value.Content;
                if (content.Count == 0)
                {
                    _logger.LogWarning("AI returned empty content for filename '{Filename}' (FinishReason: {FinishReason})", filename, finishReason);

                    return null;
                }

                if (finishReason == ChatFinishReason.ContentFilter)
                {
                    _logger.LogWarning("AI response was filtered for filename '{Filename}'", filename);

                    return null;
                }

                return content[0].Text?.Trim();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (attempt < MaxRetries)
                {
                    _logger.LogWarning(ex, "AI title attempt {Attempt} failed for '{Filename}', retrying", attempt + 1, filename);

                    continue;
                }

                _logger.LogWarning(ex, "Failed to suggest title for filename '{Filename}' after {Attempts} attempts", filename, attempt + 1);

                return null;
            }
        }

        return null;
    }
}
