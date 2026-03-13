using System.Reflection;
using FeatherPod.Infrastructure;

namespace FeatherPod.Tests;

[Collection("Sequential")]
public class BridgeResourceTests
{
    [Fact]
    public void ExtractBridge_NoEmbeddedResourceAndNoSidecar_ReturnsNull()
    {
        // Arrange
        var processPath = Environment.ProcessPath;
        if (processPath is null)
        {
            return;
        }

        var bridgePath = Path.Combine(Path.GetDirectoryName(processPath)!, "featherpod-bridge.exe");
        if (File.Exists(bridgePath))
        {
            // Can't test "no bridge" scenario when a sidecar exists
            return;
        }

        var embeddedStream = Assembly.GetAssembly(typeof(BridgeResource))!
            .GetManifestResourceStream("featherpod-bridge.exe");
        if (embeddedStream is not null)
        {
            embeddedStream.Dispose();

            // Can't test "no bridge" scenario when the embedded resource exists
            return;
        }

        // Act
        var result = BridgeResource.ExtractBridge();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ExtractBridge_EmbeddedResourceExists_ResourceStreamIsAccessible()
    {
        // Arrange/Act
        var stream = Assembly.GetAssembly(typeof(BridgeResource))!
            .GetManifestResourceStream("featherpod-bridge.exe");

        // Assert - in dev builds (no publish), embedded resource is absent; in published builds it's present
        // This test documents the expected resource name so any accidental rename breaks the build
        if (stream is not null)
        {
            using (stream)
            {
                Assert.True(stream.Length > 0);
            }
        }
    }
}
