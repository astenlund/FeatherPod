using FeatherPod.Shared.Services;

namespace FeatherPod.Tests;

public class NativeBinaryPathsTests
{
    private const string AzureHome = "azure-home";
    private const string LocalAppData = "local-app-data";
    private const string UserProfile = "user-profile";
    private const string XdgDataHome = "xdg-data-home";

    [Fact]
    public void ExecutableFileName_AppendsExeOnlyOnWindows()
    {
        // Act
        var result = NativeBinaryPaths.ExecutableFileName("ffprobe");

        // Assert
        Assert.Equal(OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe", result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetToolDirectory_AzureWithHome_UsesHomeFeatherPodFolder(bool isWindows)
    {
        // Arrange
        var variables = new Dictionary<string, string?> { ["WEBSITE_SITE_NAME"] = "site", ["HOME"] = AzureHome, ["XDG_DATA_HOME"] = XdgDataHome };

        // Act
        var result = Resolve(variables, isWindows);

        // Assert
        Assert.Equal(Path.Combine(AzureHome, ".featherpod", "some-tool"), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetToolDirectory_AzureWithoutHome_FallsThroughToPlatformDefault(string? home)
    {
        // Arrange
        var variables = new Dictionary<string, string?> { ["WEBSITE_SITE_NAME"] = "site", ["HOME"] = home };

        // Act
        var result = Resolve(variables, isWindows: true);

        // Assert
        Assert.Equal(Path.Combine(LocalAppData, "FeatherPod", "some-tool"), result);
    }

    [Fact]
    public void GetToolDirectory_WindowsWithoutAzure_UsesLocalAppDataAndIgnoresXdg()
    {
        // Arrange
        var variables = new Dictionary<string, string?> { ["HOME"] = AzureHome, ["XDG_DATA_HOME"] = XdgDataHome };

        // Act
        var result = Resolve(variables, isWindows: true);

        // Assert
        Assert.Equal(Path.Combine(LocalAppData, "FeatherPod", "some-tool"), result);
    }

    [Fact]
    public void GetToolDirectory_NonWindowsWithXdgDataHome_UsesXdgDataHome()
    {
        // Arrange
        var variables = new Dictionary<string, string?> { ["XDG_DATA_HOME"] = XdgDataHome };

        // Act
        var result = Resolve(variables, isWindows: false);

        // Assert
        Assert.Equal(Path.Combine(XdgDataHome, "FeatherPod", "some-tool"), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetToolDirectory_NonWindowsWithoutXdgDataHome_UsesLocalShare(string? xdgDataHome)
    {
        // Arrange
        var variables = new Dictionary<string, string?> { ["XDG_DATA_HOME"] = xdgDataHome };

        // Act
        var result = Resolve(variables, isWindows: false);

        // Assert
        Assert.Equal(Path.Combine(UserProfile, ".local", "share", "FeatherPod", "some-tool"), result);
    }

    [Fact]
    public void GetToolDirectory_BinaryManagersUseTheirToolFolders()
    {
        // Act
        var ffmpegDirectory = FFmpegBinaryManager.GetBinaryDirectory();
        var ytDlpDirectory = YtDlpBinaryManager.GetBinaryDirectory();

        // Assert
        Assert.Equal(NativeBinaryPaths.GetToolDirectory("ffmpeg"), ffmpegDirectory);
        Assert.Equal(NativeBinaryPaths.GetToolDirectory("yt-dlp"), ytDlpDirectory);
    }

    private static string Resolve(Dictionary<string, string?> variables, bool isWindows)
    {
        return NativeBinaryPaths.GetToolDirectory("some-tool", name => variables.GetValueOrDefault(name), isWindows, GetFakeFolderPath);
    }

    private static string GetFakeFolderPath(Environment.SpecialFolder folder)
    {
        return folder switch
        {
            Environment.SpecialFolder.LocalApplicationData => LocalAppData,
            Environment.SpecialFolder.UserProfile => UserProfile,
            _ => throw new ArgumentOutOfRangeException(nameof(folder), folder, "Unexpected special folder"),
        };
    }
}
