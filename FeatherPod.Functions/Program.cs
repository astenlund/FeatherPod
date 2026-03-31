using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using FeatherPod.Functions;
using FeatherPod.Shared.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Application Insights
builder.Services.AddApplicationInsightsTelemetryWorkerService();

// Configuration
builder.Services.Configure<FunctionSettings>(options =>
{
    options.StorageAccountName = Environment.GetEnvironmentVariable("StorageAccountName")
        ?? throw new InvalidOperationException("StorageAccountName environment variable is required");
    options.ContainerName = Environment.GetEnvironmentVariable("ContainerName") ?? "featherpod";
    options.AppServiceUrl = Environment.GetEnvironmentVariable("AppServiceUrl");
    options.InternalKey = Environment.GetEnvironmentVariable("InternalKey");
    if (int.TryParse(Environment.GetEnvironmentVariable("JobRetentionDays"), out var jobRetention))
    {
        options.JobRetentionDays = jobRetention;
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("OrphanedBlobRetentionDays"), out var blobRetention))
    {
        options.OrphanedBlobRetentionDays = blobRetention;
    }
    options.CleanupSchedule = Environment.GetEnvironmentVariable("CleanupSchedule") ?? options.CleanupSchedule;
    options.AzureOpenAIEndpoint = Environment.GetEnvironmentVariable("AzureOpenAIEndpoint");
    options.WhisperDeployment = Environment.GetEnvironmentVariable("WhisperDeployment");
    if (int.TryParse(Environment.GetEnvironmentVariable("WhisperChunkMinutes"), out var chunkMinutes))
    {
        options.WhisperChunkMinutes = chunkMinutes;
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("WhisperOverlapSeconds"), out var overlapSeconds))
    {
        options.WhisperOverlapSeconds = overlapSeconds;
    }
});

// Azure Storage clients
builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<FunctionSettings>>().Value;
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

    if (!string.IsNullOrEmpty(connectionString))
    {
        return new BlobServiceClient(connectionString);
    }

    var credential = new DefaultAzureCredential();
    var blobUri = new Uri($"https://{settings.StorageAccountName}.blob.core.windows.net");

    return new BlobServiceClient(blobUri, credential);
});

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<FunctionSettings>>().Value;
    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

    if (!string.IsNullOrEmpty(connectionString))
    {
        return new TableServiceClient(connectionString);
    }

    var credential = new DefaultAzureCredential();
    var tableUri = new Uri($"https://{settings.StorageAccountName}.table.core.windows.net");

    return new TableServiceClient(tableUri, credential);
});

// FFmpeg for audio normalization (with blob lease for distributed locking)
builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<FunctionSettings>>().Value;
    var blobServiceClient = sp.GetRequiredService<BlobServiceClient>();
    var blobContainer = blobServiceClient.GetBlobContainerClient(settings.ContainerName);
    var logger = sp.GetRequiredService<ILogger<FFmpegBinaryManager>>();

    return new FFmpegBinaryManager(logger, blobContainer);
});

builder.Services.AddSingleton<IAudioNormalizationService, AudioNormalizationService>();

// Whisper transcription (optional, configured via AzureOpenAIEndpoint + WhisperDeployment env vars)
builder.Services.AddSingleton<ITranscriptionService>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<FunctionSettings>>().Value;
    var logger = sp.GetRequiredService<ILogger<TranscriptionService>>();

    return new TranscriptionService(settings.AzureOpenAIEndpoint, settings.WhisperDeployment, logger, settings.WhisperChunkMinutes, settings.WhisperOverlapSeconds);
});

// HttpClient for App Service cache refresh
builder.Services.AddHttpClient();

builder.Build().Run();
