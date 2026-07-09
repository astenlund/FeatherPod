using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatherPod.Shared;

namespace FeatherPod.Infrastructure;

internal sealed class LocalFileServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private const int MaxStartAttempts = 3;

    private readonly string _allowedOrigin;
    private readonly List<(string Path, string Name, long Size)> _files = [];
    private readonly HashSet<int> _uploadedIndices = [];
    private readonly List<HttpListenerResponse> _sseClients = [];
    private readonly object _lock = new();

    private HttpListener? _listener;
    private Timer? _idleTimer;
    private Timer? _heartbeatTimer;
    private bool _disposed;

    public int Port { get; private set; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public event Action? OnIdleTimeout;

    public LocalFileServer(string allowedOrigin)
    {
        _allowedOrigin = allowedOrigin;
    }

    public void Start()
    {
        for (var attempt = 0; attempt < MaxStartAttempts; attempt++)
        {
            var port = FindFreePort();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                listener.Start();
                _listener = listener;
                Port = port;

                _idleTimer = new Timer(_ => HandleIdleTimeout(), null, IdleTimeout, Timeout.InfiniteTimeSpan);
                _heartbeatTimer = new Timer(_ => SendHeartbeat(), null, HeartbeatInterval, HeartbeatInterval);

                Task.Run(ProcessRequests);

                return;
            }
            catch (HttpListenerException) when (attempt < MaxStartAttempts - 1)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Failed to start local file server after multiple attempts.");
    }

    public (int Index, string Name, long Size) AddFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        var extension = Path.GetExtension(filePath);
        if (!AudioExtensions.All.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported file extension: {extension}");
        }

        var name = Path.GetFileName(filePath);
        var size = new FileInfo(filePath).Length;
        int index;

        lock (_lock)
        {
            index = _files.Count;
            _files.Add((filePath, name, size));
        }

        ResetIdleTimer();
        BroadcastSseEvent("new-file", new { index, name, size });

        return (index, name, size);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _idleTimer?.Dispose();
        _heartbeatTimer?.Dispose();

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_lock)
        {
            foreach (var client in _sseClients)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }

            _sseClients.Clear();
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    private async Task ProcessRequests()
    {
        while (_listener is { IsListening: true })
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            AddCorsHeaders(response);

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();

                return;
            }

            var path = request.Url?.AbsolutePath ?? "";
            var token = request.QueryString["token"];

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(token ?? ""),
                    Encoding.UTF8.GetBytes(Token)))
            {
                response.StatusCode = 403;
                response.Close();

                return;
            }

            switch (request.HttpMethod)
            {
                case "GET" when path == "/api/files":
                    HandleGetFiles(response);
                    break;
                case "GET" when path.StartsWith("/api/files/") && int.TryParse(path["/api/files/".Length..], out var index):
                    HandleGetFileByIndex(response, index);
                    break;
                case "GET" when path == "/api/events":
                    HandleSseConnection(response);

                    return; // Don't close response -- SSE keeps it open
                case "POST" when path == "/api/heartbeat":
                    ResetIdleTimer();
                    response.StatusCode = 200;
                    response.Close();
                    break;
                case "POST" when path.EndsWith("/uploaded") && path.StartsWith("/api/files/"):
                    HandleFileUploaded(response, path);
                    break;
                case "POST" when path == "/api/files":
                    HandlePostFile(request, response);
                    break;
                default:
                    response.StatusCode = 404;
                    response.Close();
                    break;
            }
        }
        catch (Exception)
        {
            try
            {
                response.StatusCode = 500;
                response.Close();
            }
            catch
            {
            }
        }
    }

    private void HandleGetFiles(HttpListenerResponse response)
    {
        List<object> fileList;

        lock (_lock)
        {
            fileList = _files.Select(f => (object)new { name = f.Name, size = f.Size }).ToList();
        }

        WriteJson(response, 200, fileList);
    }

    private void HandleGetFileByIndex(HttpListenerResponse response, int index)
    {
        string path;
        string name;
        long size;

        lock (_lock)
        {
            if (index < 0 || index >= _files.Count)
            {
                response.StatusCode = 404;
                response.Close();

                return;
            }

            path = _files[index].Path;
            name = _files[index].Name;
            size = _files[index].Size;
        }

        response.StatusCode = 200;
        response.ContentType = AudioHelper.GetMimeType(name);
        response.ContentLength64 = size;
        response.AddHeader("Content-Disposition", $"attachment; filename=\"{name}\"");

        using var fileStream = File.OpenRead(path);
        fileStream.CopyTo(response.OutputStream);
        response.Close();

        ResetIdleTimer();
    }

    private void HandlePostFile(HttpListenerRequest request, HttpListenerResponse response)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = reader.ReadToEnd();

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException)
        {
            response.StatusCode = 400;
            response.Close();

            return;
        }

        if (!json.TryGetProperty("path", out var pathElement))
        {
            response.StatusCode = 400;
            response.Close();

            return;
        }

        var filePath = pathElement.GetString();
        if (string.IsNullOrEmpty(filePath))
        {
            response.StatusCode = 400;
            response.Close();

            return;
        }

        int index;
        string name;
        long size;

        try
        {
            (index, name, size) = AddFile(filePath);
        }
        catch (FileNotFoundException)
        {
            WriteJson(response, 400, new { error = "File not found." });

            return;
        }
        catch (ArgumentException ex)
        {
            WriteJson(response, 400, new { error = ex.Message });

            return;
        }

        WriteJson(response, 201, new { index, name, size });
    }

    private void HandleFileUploaded(HttpListenerResponse response, string path)
    {
        var indexStr = path["/api/files/".Length..^"/uploaded".Length];
        if (!int.TryParse(indexStr, out var index))
        {
            response.StatusCode = 404;
            response.Close();

            return;
        }

        lock (_lock)
        {
            if (index < 0 || index >= _files.Count)
            {
                response.StatusCode = 404;
                response.Close();

                return;
            }

            _uploadedIndices.Add(index);
        }

        ResetIdleTimer();
        response.StatusCode = 200;
        response.Close();
    }

    public List<string> GetUploadedFilePaths()
    {
        lock (_lock)
        {
            return [.. _uploadedIndices.Where(i => i < _files.Count).Select(i => _files[i].Path)];
        }
    }

    private void HandleSseConnection(HttpListenerResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.Add("Cache-Control", "no-cache");
        response.SendChunked = true;
        response.StatusCode = 200;

        // Write an initial SSE comment to force headers + first chunk to be sent
        var initBytes = Encoding.UTF8.GetBytes(": connected\n\n");
        response.OutputStream.Write(initBytes, 0, initBytes.Length);
        response.OutputStream.Flush();

        // Send existing files as initial events
        lock (_lock)
        {
            for (var i = 0; i < _files.Count; i++)
            {
                var file = _files[i];
                var eventData = JsonSerializer.Serialize(new { index = i, name = file.Name, size = file.Size }, JsonOptions);

                try
                {
                    WriteSseEvent(response, "new-file", eventData);
                }
                catch
                {
                    try { response.Close(); } catch { }

                    return;
                }
            }

            _sseClients.Add(response);
        }

        ResetIdleTimer();
    }

    private void BroadcastSseEvent(string eventType, object data)
    {
        var eventData = JsonSerializer.Serialize(data, JsonOptions);
        List<HttpListenerResponse> deadClients = [];

        lock (_lock)
        {
            foreach (var client in _sseClients)
            {
                try
                {
                    WriteSseEvent(client, eventType, eventData);
                }
                catch
                {
                    deadClients.Add(client);
                }
            }

            foreach (var dead in deadClients)
            {
                _sseClients.Remove(dead);
            }
        }
    }

    private void SendHeartbeat()
    {
        BroadcastSseEvent("heartbeat", new { });
    }

    private static void WriteSseEvent(HttpListenerResponse response, string eventType, string data)
    {
        var message = $"event: {eventType}\ndata: {data}\n\n";
        var bytes = Encoding.UTF8.GetBytes(message);
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Flush();
    }

    private static void WriteJson(HttpListenerResponse response, int statusCode, object data)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.Close();
    }

    private void AddCorsHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Access-Control-Allow-Origin", _allowedOrigin);
        response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
    }

    private void ResetIdleTimer()
    {
        _idleTimer?.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
    }

    private void HandleIdleTimeout()
    {
        var handler = OnIdleTimeout;
        Dispose();
        handler?.Invoke();
    }
}
