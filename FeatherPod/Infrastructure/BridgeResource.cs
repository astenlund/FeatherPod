using System.Reflection;

namespace FeatherPod.Infrastructure;

internal static class BridgeResource
{
    private const string BridgeFileName = "featherpod-bridge.exe";
    private const string ResourceName = "featherpod-bridge.exe";

    internal static string? ExtractBridge()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is null)
        {
            return null;
        }

        var targetDir = Path.GetDirectoryName(processPath)!;
        var targetPath = Path.Combine(targetDir, BridgeFileName);

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is not null)
        {
            try
            {
                var tempPath = targetPath + ".tmp";
                using (stream)
                using (var fileStream = File.Create(tempPath))
                {
                    stream.CopyTo(fileStream);
                }

                File.Move(tempPath, targetPath, overwrite: true);

                return targetPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(targetPath))
                {
                    return targetPath;
                }

                return null;
            }
        }

        if (File.Exists(targetPath))
        {
            return targetPath;
        }

        return null;
    }
}
