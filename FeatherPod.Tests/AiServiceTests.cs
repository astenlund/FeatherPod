using FeatherPod.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class AiServiceTests
{
    [Fact]
    public void IsAvailable_ShouldReturnFalse_WhenConfigurationMissing()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Error)).CreateLogger<AiService>();

        // Act
        var service = new AiService(config, logger);

        // Assert
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public async Task SuggestTitleAsync_ShouldReturnNull_WhenNotConfigured()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Error)).CreateLogger<AiService>();
        var service = new AiService(config, logger);

        // Act
        var result = await service.SuggestTitleAsync("test_file.mp3");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void IsAvailable_ShouldReturnFalse_WhenEndpointEmptyString()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "",
                ["AzureOpenAI:Deployment"] = "gpt-5.4-nano",
            })
            .Build();
        var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Error)).CreateLogger<AiService>();

        // Act
        var service = new AiService(config, logger);

        // Assert
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public void IsAvailable_ShouldReturnFalse_WhenDeploymentEmptyString()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://test.openai.azure.com/",
                ["AzureOpenAI:Deployment"] = "",
            })
            .Build();
        var logger = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Error)).CreateLogger<AiService>();

        // Act
        var service = new AiService(config, logger);

        // Assert
        Assert.False(service.IsAvailable);
    }
}
