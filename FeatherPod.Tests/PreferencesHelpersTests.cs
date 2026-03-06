using System.Text.Json.Nodes;
using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class PreferencesHelpersTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testPreferencesPath;

    public PreferencesHelpersTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"FeatherPodTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
        _testPreferencesPath = Path.Combine(_testDirectory, "preferences.json");

        // Override directory for tests
        PreferencesHelpers.PreferencesDirectoryOverride = _testDirectory;
    }

    public void Dispose()
    {
        PreferencesHelpers.PreferencesDirectoryOverride = null;
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public void GetEnableAdminFeatures_ReturnsNull_WhenFileDoesNotExist()
    {
        var result = PreferencesHelpers.GetEnableAdminFeatures();
        Assert.Null(result);
    }

    [Fact]
    public void GetEnableAdminFeatures_ReturnsNull_WhenPropertyNotSet()
    {
        File.WriteAllText(_testPreferencesPath, """{ "Environments": {} }""");
        var result = PreferencesHelpers.GetEnableAdminFeatures();
        Assert.Null(result);
    }

    [Fact]
    public void GetEnableAdminFeatures_ReturnsTrue_WhenSetToTrue()
    {
        File.WriteAllText(_testPreferencesPath, """{ "EnableAdminFeatures": true }""");
        var result = PreferencesHelpers.GetEnableAdminFeatures();
        Assert.True(result);
    }

    [Fact]
    public void GetEnableAdminFeatures_ReturnsFalse_WhenSetToFalse()
    {
        File.WriteAllText(_testPreferencesPath, """{ "EnableAdminFeatures": false }""");
        var result = PreferencesHelpers.GetEnableAdminFeatures();
        Assert.False(result);
    }

    [Fact]
    public void SetEnableAdminFeatures_CreatesFileAndSetsValue()
    {
        PreferencesHelpers.SetEnableAdminFeatures(true);

        var content = File.ReadAllText(_testPreferencesPath);
        var root = JsonNode.Parse(content);
        Assert.True(root!["EnableAdminFeatures"]!.GetValue<bool>());
    }

    [Fact]
    public void SetEnableAdminFeatures_PreservesExistingSettings()
    {
        File.WriteAllText(_testPreferencesPath, """
        {
            "Environments": {
                "Prod": { "ApiKey": "fp_test_abc" }
            }
        }
        """);

        PreferencesHelpers.SetEnableAdminFeatures(true);

        var content = File.ReadAllText(_testPreferencesPath);
        var root = JsonNode.Parse(content);
        Assert.True(root!["EnableAdminFeatures"]!.GetValue<bool>());
        Assert.Equal("fp_test_abc", root["Environments"]!["Prod"]!["ApiKey"]!.GetValue<string>());
    }
}
