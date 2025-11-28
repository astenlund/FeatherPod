using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using FeatherPod.Functions;
using FeatherPod.Shared.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

// FFmpeg for audio normalization
builder.Services.AddSingleton<FFmpegBinaryManager>();
builder.Services.AddSingleton<IAudioNormalizationService, AudioNormalizationService>();

// HttpClient for App Service cache refresh
builder.Services.AddHttpClient();

builder.Build().Run();
