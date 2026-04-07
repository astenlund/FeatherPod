using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using FeatherPod.Shared;

namespace FeatherPod.Infrastructure;

internal record LockFileInfo
{
    required public int Port { get; init; }
    required public string Token { get; init; }
    required public string FeedId { get; init; }
    required public int Pid { get; init; }
}

[SupportedOSPlatform("windows")]
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(2);

    private readonly string _feedId;
    private readonly string _lockFilePath;

    private Mutex? _mutex;
    private bool _isMutexOwner;

    public SingleInstanceCoordinator(string feedId)
    {
        _feedId = feedId;

        var directory = PreferencesHelpers.PreferencesDirectoryOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FeatherPod");

        _lockFilePath = Path.Combine(directory, $"context-menu-server-{feedId}.json");
    }

    public bool TryBecomeHost(out LockFileInfo? existingHost)
    {
        existingHost = null;

        _mutex = new Mutex(initiallyOwned: false, $@"Global\FeatherPod.ContextMenu.{_feedId}");

        try
        {
            try
            {
                _isMutexOwner = _mutex.WaitOne(MutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                _isMutexOwner = true;
            }

            if (!_isMutexOwner)
            {
                return false;
            }

            var lockInfo = ReadLockFile();
            if (lockInfo is not null)
            {
                if (ValidateExistingHost(lockInfo))
                {
                    existingHost = lockInfo;
                    ReleaseMutex();

                    return false;
                }

                DeleteLockFile();
            }

            return true;
        }
        catch
        {
            ReleaseMutex();

            throw;
        }
    }

    public void WriteLockFile(int port, string token)
    {
        var directory = Path.GetDirectoryName(_lockFilePath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var info = new LockFileInfo
        {
            Port = port,
            Token = token,
            FeedId = _feedId,
            Pid = Environment.ProcessId,
        };

        File.WriteAllText(_lockFilePath, JsonSerializer.Serialize(info, JsonOptions));
        ReleaseMutex();
    }

    public LockFileInfo? ReadLockFile()
    {
        if (!File.Exists(_lockFilePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_lockFilePath);

            return JsonSerializer.Deserialize<LockFileInfo>(json, JsonOptions);
        }
        catch
        {
            // Corrupt or unreadable lock file; treat as absent so a fresh host can take over.
            return null;
        }
    }

    public void DeleteLockFile()
    {
        FileHelper.TryDeleteFile(_lockFilePath);
    }

    public void Dispose()
    {
        ReleaseMutex();

        try
        {
            _mutex?.Dispose();
        }
        catch
        {
            // Mutex may already be disposed or owned by another holder; nothing actionable on shutdown.
        }

        _mutex = null;
    }

    private static bool ValidateExistingHost(LockFileInfo info)
    {
        try
        {
            var process = Process.GetProcessById(info.Pid);
            if (process.HasExited)
            {
                return false;
            }
        }
        catch
        {
            // Process not found or access denied; existing host is gone.
            return false;
        }

        try
        {
            using var client = new HttpClient { Timeout = HealthCheckTimeout };
            var response = client.GetAsync($"http://127.0.0.1:{info.Port}/api/files?token={info.Token}").GetAwaiter().GetResult();

            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Health check failed (timeout, refused, network); existing host is unreachable.
            return false;
        }
    }

    private void ReleaseMutex()
    {
        if (_isMutexOwner)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch
            {
                // Not currently owned or already released; clear ownership flag regardless.
            }

            _isMutexOwner = false;
        }
    }
}
