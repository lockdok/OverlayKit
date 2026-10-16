using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverlayKit;

internal sealed class OverlayServer : IAsyncDisposable
{
    private const int Port = 17893;
    private const int MaxMessageSize = 16 * 1024;
    private const int MaxHeaderSize = 16 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private readonly ConcurrentDictionary<Guid, BrowserClient> _clients = new();
    private readonly object _settingsLock = new();
    private OverlaySettings _settings = OverlaySettings.Load();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        Console.WriteLine($"Local server listening at http://127.0.0.1:{Port}/");
        using var registration = cancellationToken.Register(_listener.Stop);

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = HandleClientAsync(client);
        }
    }

    public void PublishFromHook(object message)
    {
        _ = PublishSafelyAsync(message);
    }

    private async Task PublishSafelyAsync(object message)
    {
        try
        {
            await BroadcastAsync(message);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not publish an input event: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request.Method != "GET")
                {
                    await WriteResponseAsync(stream, "405 Method Not Allowed", "text/plain; charset=utf-8", "Method not allowed.");
                    return;
                }

                if (request.Path == "/ws"
                    && request.Headers.TryGetValue("Upgrade", out var upgrade)
                    && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketAsync(stream, request.Headers);
                    return;
                }

                await ServeFileAsync(stream, request.Path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Browser connection failed: {ex.Message}");
            }
        }
    }

    private static async Task<BrowserRequest> ReadRequestAsync(NetworkStream stream)
    {
        using var headers = new MemoryStream();
        var oneByte = new byte[1];
        var complete = false;
        while (headers.Length < MaxHeaderSize)
        {
            var count = await stream.ReadAsync(oneByte, 0, 1);
            if (count == 0)
            {
                throw new IOException("The browser disconnected before sending an HTTP request.");
            }

            headers.WriteByte(oneByte[0]);
            if (headers.Length >= 4)
            {
                var bytes = headers.GetBuffer();
                var length = (int)headers.Length;
                if (bytes[length - 4] == '\r' && bytes[length - 3] == '\n'
                    && bytes[length - 2] == '\r' && bytes[length - 1] == '\n')
                {
                    complete = true;
                    break;
                }
            }
        }

        if (!complete)
        {
            throw new InvalidDataException("The HTTP request headers exceeded the size limit.");
        }

        var lines = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
        {
            throw new FormatException("The browser sent a malformed HTTP request line.");
        }

        if (!Uri.TryCreate($"http://127.0.0.1{requestLine[1]}", UriKind.Absolute, out var uri))
        {
            throw new FormatException("The browser sent an invalid request path.");
        }

        var parsedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                parsedHeaders[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return new BrowserRequest(requestLine[0], uri.AbsolutePath, parsedHeaders);
    }

    private static async Task ServeFileAsync(NetworkStream stream, string path)
    {
        var fileName = path switch
        {
            "/" => "index.html",
            "/index.html" => "index.html",
            "/app.js" => "app.js",
            "/styles.css" => "styles.css",
            _ => null
        };

        if (fileName is null)
        {
            await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found.");
            return;
        }

        var filePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", fileName);
        if (!File.Exists(filePath))
        {
            await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Static file not found.");
            Console.Error.WriteLine($"Static file is missing: {filePath}");
            return;
        }

        var body = await File.ReadAllBytesAsync(filePath);
        var contentType = fileName switch
        {
            "index.html" => "text/html; charset=utf-8",
            "app.js" => "text/javascript; charset=utf-8",
            _ => "text/css; charset=utf-8"
        };
        await WriteResponseAsync(stream, "200 OK", contentType, body);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, string content)
    {
        await WriteResponseAsync(stream, status, contentType, Encoding.UTF8.GetBytes(content));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, byte[] body)
    {
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, 0, headers.Length);
        await stream.WriteAsync(body, 0, body.Length);
        await stream.FlushAsync();
    }

    private async Task HandleWebSocketAsync(NetworkStream stream, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Origin", out var origin)
            || !origin.Equals($"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, "403 Forbidden", "text/plain; charset=utf-8", "WebSocket origin is not allowed.");
            return;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key)
            || !headers.TryGetValue("Sec-WebSocket-Version", out var version)
            || version != "13")
        {
            await WriteResponseAsync(stream, "400 Bad Request", "text/plain; charset=utf-8", "Invalid WebSocket handshake.");
            return;
        }

        if (!headers.TryGetValue("Connection", out var connection)
            || !connection.Split(',').Any(part => part.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase)))
        {
            await WriteResponseAsync(stream, "400 Bad Request", "text/plain; charset=utf-8", "Invalid WebSocket connection header.");
            return;
        }

        var acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {acceptKey}\r\n\r\n");
        await stream.WriteAsync(response, 0, response.Length);
        await stream.FlushAsync();

        using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
        var id = Guid.NewGuid();
        var client = new BrowserClient(socket);
        _clients[id] = client;
        Console.WriteLine($"Browser connected ({_clients.Count} connected).");

        try
        {
            await client.SendAsync(new { type = "settings", settings = GetSettings() });
            await ReceiveMessagesAsync(id, client);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
            Console.WriteLine($"Browser disconnected ({_clients.Count} connected).");
        }
    }

    private async Task ReceiveMessagesAsync(Guid id, BrowserClient client)
    {
        var buffer = new byte[4096];
        while (client.Socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await client.Socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Connection closed",
                        CancellationToken.None);
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await client.SendAsync(new { type = "error", message = "Only text WebSocket messages are supported." });
                    await client.Socket.CloseAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "Text messages are required",
                        CancellationToken.None);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageSize)
                {
                    await client.SendAsync(new { type = "error", message = "The message exceeded the size limit." });
                    return;
                }
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text || message.Length > MaxMessageSize)
            {
                continue;
            }

            await ApplyCommandAsync(id, client, message.ToArray());
        }
    }

    private async Task ApplyCommandAsync(Guid id, BrowserClient client, byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "settings")
            {
                await client.SendAsync(new { type = "error", message = "Unsupported command.", settings = GetSettings() });
                return;
            }

            if (!root.TryGetProperty("settings", out var settingsElement)
                || settingsElement.ValueKind != JsonValueKind.Object)
            {
                await client.SendAsync(new { type = "error", message = "Settings must be an object.", settings = GetSettings() });
                return;
            }

            OverlaySettings updated;
            string? validationError = null;
            lock (_settingsLock)
            {
                var current = _settings;
                if (!TryReadSettings(settingsElement, current, out updated, out var error))
                {
                    validationError = error;
                }
                else
                {
                    updated = OverlaySettings.Sanitize(updated);
                    updated.Save();
                    _settings = updated;
                }
            }

            if (validationError is not null)
            {
                await client.SendAsync(new { type = "error", message = validationError, settings = GetSettings() });
                return;
            }

            await BroadcastAsync(new { type = "settings", settings = updated });
            Console.WriteLine($"Settings updated by browser {id}.");
        }
        catch (JsonException ex)
        {
            await client.SendAsync(new { type = "error", message = $"Invalid JSON: {ex.Message}", settings = GetSettings() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not save overlay settings: {ex.Message}");
            await client.SendAsync(new { type = "error", message = "Settings could not be saved. Check the app data folder permissions.", settings = GetSettings() });
        }
    }

    private static bool TryReadSettings(
        JsonElement element,
        OverlaySettings current,
        out OverlaySettings updated,
        out string error)
    {
        updated = current;
        error = "Invalid settings value.";

        var mode = current.DisplayMode;
        if (element.TryGetProperty("displayMode", out var modeElement))
        {
            if (modeElement.ValueKind != JsonValueKind.String
                || modeElement.GetString() is not ("text" or "keyboard"))
            {
                return false;
            }

            mode = modeElement.GetString()!;
        }

        if (!TryGetBool(element, "showKeyboard", current.ShowKeyboard, out var showKeyboard)
            || !TryGetBool(element, "showMouse", current.ShowMouse, out var showMouse)
            || !TryGetBool(element, "overlayVisible", current.OverlayVisible, out var overlayVisible))
        {
            return false;
        }

        var fadeMilliseconds = current.FadeMilliseconds;
        if (element.TryGetProperty("fadeMilliseconds", out var fadeElement))
        {
            if (!fadeElement.TryGetInt32(out fadeMilliseconds)
                || fadeMilliseconds is < 250 or > 5000)
            {
                error = "Fade time must be between 250 and 5000 milliseconds.";
                return false;
            }
        }

        updated = new OverlaySettings
        {
            DisplayMode = mode,
            ShowKeyboard = showKeyboard,
            ShowMouse = showMouse,
            OverlayVisible = overlayVisible,
            FadeMilliseconds = fadeMilliseconds
        };
        error = "";
        return true;
    }

    private static bool TryGetBool(JsonElement element, string name, bool current, out bool value)
    {
        value = current;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private OverlaySettings GetSettings()
    {
        lock (_settingsLock)
        {
            return _settings;
        }
    }

    private async Task BroadcastAsync(object message)
    {
        var clients = _clients.ToArray();
        await Task.WhenAll(clients.Select(async item =>
        {
            try
            {
                await item.Value.SendAsync(message);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                _clients.TryRemove(item.Key, out _);
                item.Value.Dispose();
            }
        }));
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
        return ValueTask.CompletedTask;
    }

    private sealed class BrowserClient : IDisposable
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public BrowserClient(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }

        public async Task SendAsync(object message)
        {
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, SerializerOptions));
            await _sendLock.WaitAsync();
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(
                        new ArraySegment<byte>(payload),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            Socket.Dispose();
        }
    }

    private sealed record BrowserRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers);
}
