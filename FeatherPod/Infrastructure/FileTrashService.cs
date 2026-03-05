using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FeatherPod.Infrastructure;

internal static class FileTrashService
{
    internal record DeleteResult(bool Success, string? Method = null, string? Error = null);

    internal static DeleteResult TryDeleteFile(string filePath, bool useTrash)
    {
        if (!File.Exists(filePath))
        {
            return new(false, Error: "File not found");
        }

        if (useTrash)
        {
            var trashResult = TrySendToTrash(filePath);
            if (trashResult.Success)
            {
                return trashResult;
            }

            // Trash failed - fall back to permanent delete but indicate this in the method
            var fallbackResult = TryPermanentDelete(filePath);
            if (fallbackResult.Success)
            {
                return fallbackResult with { Method = "permanently deleted (trash unavailable)" };
            }

            return fallbackResult;
        }

        return TryPermanentDelete(filePath);
    }

    private static DeleteResult TrySendToTrash(string filePath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return TrySendToTrashWindows(filePath);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return TryRunTrashCommand("gio", ["trash", filePath], filePath);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return TryRunTrashCommand("trash", [filePath], filePath);
            }

            return new(false, Error: "Unsupported platform for trash");
        }
        catch (Exception ex)
        {
            return new(false, Error: ex.Message);
        }
    }

    private static DeleteResult TrySendToTrashWindows(string filePath)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                filePath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return new(true, Method: "sent to trash");
        }
        catch
        {
            return new(false, Error: "Failed to send to Recycle Bin");
        }
    }

    private static DeleteResult TryRunTrashCommand(string command, string[] arguments, string filePath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);

            if (process == null)
            {
                return new(false, Error: $"{command} not available");
            }

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); }
                catch { /* ignore */ }
                return new(false, Error: $"{command} timed out");
            }

            if (process.ExitCode == 0 && !File.Exists(filePath))
            {
                return new(true, Method: "sent to trash");
            }

            return new(false, Error: $"{command} returned exit code {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return new(false, Error: ex.Message);
        }
    }

    private static DeleteResult TryPermanentDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
            return new(true, Method: "permanently deleted");
        }
        catch (Exception ex)
        {
            return new(false, Error: ex.Message);
        }
    }
}
