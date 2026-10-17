using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace OverlayKit;

internal sealed class OverlayServer : IAsyncDisposable
{
    private const int MaxMessageSize = 16 * 1024;
    private const int MaxHeaderSize = 16 * 1024;
    private const int MaxConcurrentConnections = 16;
    private const int MaxQueuedMessagesPerClient = 512;
    private const int MaxQueuedInputEvents = 256;
    private static readonly TimeSpan RequestHeaderTimeout = TimeSpan.FromSeconds(5);
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _connectionSlots = new(MaxConcurrentConnections);
    private readonly ConcurrentDictionary<Guid, BrowserClient> _clients = new();
    private readonly Channel<InputEvent> _inputEvents = Channel.CreateBounded<InputEvent>(
        new BoundedChannelOptions(MaxQueuedInputEvents)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly object _settingsLock = new();
    private OverlaySettings _settings = OverlaySettings.Load();

    public OverlayServer()
    {
        var portValue = Environment.GetEnvironmentVariable("OVERLAYKIT_PORT");
        var port = 17893;
        if (!string.IsNullOrWhiteSpace(portValue)
            && (!int.TryParse(portValue, out port) || port is < 1024 or > 65535))
        {
            throw new InvalidOperationException("OVERLAYKIT_PORT must be an integer from 1024 through 65535.");
        }

        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, Port);
    }

    public int Port { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        Console.WriteLine($"Local server listening at http://127.0.0.1:{Port}/");
        using var registration = cancellationToken.Register(_listener.Stop);
        using var inputDispatcherStop = cancellationToken.Register(() => _inputEvents.Writer.TryComplete());
        var inputDispatcher = DispatchInputEventsAsync();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _connectionSlots.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _connectionSlots.Release();
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    _connectionSlots.Release();
                    break;
                }
                catch
                {
                    _connectionSlots.Release();
                    throw;
                }

                _ = HandleClientAsync(client);
            }
        }
        finally
        {
            _inputEvents.Writer.TryComplete();
            await inputDispatcher;
        }
    }

    public void PublishFromHook(InputEvent message)
    {
        if (!_clients.IsEmpty)
        {
            _inputEvents.Writer.TryWrite(message);
        }
    }

    private async Task DispatchInputEventsAsync()
    {
        await foreach (var message in _inputEvents.Reader.ReadAllAsync())
        {
            Broadcast(Serialize(message));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (request, requestStream) = await ReadRequestAsync(stream);
                if (request.Method != "GET")
                {
                    await WriteResponseAsync(requestStream, "405 Method Not Allowed", "text/plain; charset=utf-8", "Method not allowed.");
                    return;
                }

                if (request.Path == "/ws"
                    && request.Headers.TryGetValue("Upgrade", out var upgrade)
                    && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketAsync(requestStream, request.Headers);
                    return;
                }

                await ServeFileAsync(requestStream, request.Path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Browser connection failed: {ex.Message}");
            }
            finally
            {
                _connectionSlots.Release();
            }
        }
    }

    private static async Task<(BrowserRequest Request, Stream Stream)> ReadRequestAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(RequestHeaderTimeout);
        using var headers = new MemoryStream();
        var buffer = new byte[1024];
        var delimiterIndex = -1;

        while (headers.Length < MaxHeaderSize)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), timeout.Token);
            if (count == 0)
            {
                throw new IOException("The browser disconnected before sending an HTTP request.");
            }

            var previousLength = (int)headers.Length;
            headers.Write(buffer, 0, count);

            var currentBytes = headers.GetBuffer();
            var totalLength = (int)headers.Length;
            var searchStart = Math.Max(0, previousLength - 3);
            for (var i = searchStart; i <= totalLength - 4; i++)
            {
                if (currentBytes[i] == '\r' && currentBytes[i + 1] == '\n'
                    && currentBytes[i + 2] == '\r' && currentBytes[i + 3] == '\n')
                {
                    delimiterIndex = i;
                    break;
                }
            }

            if (delimiterIndex >= 0)
            {
                break;
            }
        }

        if (delimiterIndex < 0)
        {
            throw new InvalidDataException("The HTTP request headers exceeded the size limit.");
        }

        var headerBytes = headers.ToArray();
        var lines = Encoding.ASCII.GetString(headerBytes, 0, delimiterIndex).Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
        {
            throw new FormatException("The browser sent a malformed HTTP request line.");
        }

        var rawTarget = requestLine[1];
        string path;
        if (rawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || rawTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(rawTarget, UriKind.Absolute, out var absoluteUri))
            {
                throw new FormatException("The browser sent an invalid request path.");
            }
            path = absoluteUri.AbsolutePath;
        }
        else
        {
            if (!Uri.TryCreate($"http://127.0.0.1{rawTarget}", UriKind.Absolute, out var uri))
            {
                throw new FormatException("The browser sent an invalid request path.");
            }
            path = uri.AbsolutePath;
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

        var request = new BrowserRequest(requestLine[0], path, parsedHeaders);
        var remainingBytesLength = (int)headers.Length - (delimiterIndex + 4);
        Stream effectiveStream = remainingBytesLength > 0
            ? new PrefixedStream(headerBytes.AsMemory(delimiterIndex + 4, remainingBytesLength).ToArray(), stream)
            : stream;

        return (request, effectiveStream);
    }

    private static async Task ServeFileAsync(Stream stream, string path)
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

    private static async Task WriteResponseAsync(Stream stream, string status, string contentType, string content)
    {
        await WriteResponseAsync(stream, status, contentType, Encoding.UTF8.GetBytes(content));
    }

    private static async Task WriteResponseAsync(Stream stream, string status, string contentType, byte[] body)
    {
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, 0, headers.Length);
        await stream.WriteAsync(body, 0, body.Length);
        await stream.FlushAsync();
    }

    private async Task HandleWebSocketAsync(Stream stream, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Origin", out var origin)
            || !IsAllowedOrigin(origin, Port))
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
        var client = new BrowserClient(socket, MaxQueuedMessagesPerClient);
        _clients[id] = client;
        Console.WriteLine($"Browser connected ({_clients.Count} connected).");

        var sendLoop = client.SendLoopAsync();
        try
        {
            client.Send(Serialize(new { type = "settings", settings = GetSettings() }));
            await ReceiveMessagesAsync(id, client);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
            try
            {
                await sendLoop;
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The connection is already being torn down.
            }
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
                client.Send(Serialize(new { type = "error", message = "Only text WebSocket messages are supported." }));
                    await client.Socket.CloseAsync(
                        WebSocketCloseStatus.InvalidMessageType,
                        "Text messages are required",
                        CancellationToken.None);
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageSize)
                {
                    client.Send(Serialize(new { type = "error", message = "The message exceeded the size limit." }));
                    await client.Socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "The message exceeded the size limit.",
                        CancellationToken.None);
                    return;
                }
            } while (!result.EndOfMessage);

            ApplyCommand(id, client, message.ToArray());
        }
    }

    private void ApplyCommand(Guid id, BrowserClient client, byte[] bytes)
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
                client.Send(Serialize(new { type = "error", message = "Unsupported command.", settings = GetSettings() }));
                return;
            }

            if (!root.TryGetProperty("settings", out var settingsElement)
                || settingsElement.ValueKind != JsonValueKind.Object)
            {
                client.Send(Serialize(new { type = "error", message = "Settings must be an object.", settings = GetSettings() }));
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
                client.Send(Serialize(new { type = "error", message = validationError, settings = GetSettings() }));
                return;
            }

            Broadcast(Serialize(new { type = "settings", settings = updated }));
            Console.WriteLine($"Settings updated by browser {id}.");
        }
        catch (JsonException ex)
        {
            client.Send(Serialize(new { type = "error", message = $"Invalid JSON: {ex.Message}", settings = GetSettings() }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not save overlay settings: {ex.Message}");
            client.Send(Serialize(new { type = "error", message = "Settings could not be saved. Check the app data folder permissions.", settings = GetSettings() }));
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

        var positionX = current.PositionX;
        if (!TryGetInt(element, "positionX", ref positionX, 0, 100, out error))
        {
            return false;
        }

        var positionY = current.PositionY;
        if (!TryGetInt(element, "positionY", ref positionY, 0, 100, out error))
        {
            return false;
        }

        var layoutScalePercent = current.LayoutScalePercent;
        if (!TryGetInt(element, "layoutScalePercent", ref layoutScalePercent, 50, 300, out error))
        {
            return false;
        }

        var fontSize = current.FontSize;
        if (!TryGetInt(element, "fontSize", ref fontSize, 10, 36, out error))
        {
            return false;
        }

        var panelOpacity = current.PanelOpacity;
        if (!TryGetInt(element, "panelOpacity", ref panelOpacity, 0, 100, out error))
        {
            return false;
        }

        if (!TryGetString(element, "fontFamily", current.FontFamily, out var fontFamily)
            || !OverlaySettings.FontFamilies.Contains(fontFamily, StringComparer.Ordinal))
        {
            error = "Choose a font from the supported font list.";
            return false;
        }

        if (!TryGetColor(element, "textColor", current.TextColor, out var textColor)
            || !TryGetColor(element, "accentColor", current.AccentColor, out var accentColor)
            || !TryGetColor(element, "keyColor", current.KeyColor, out var keyColor)
            || !TryGetColor(element, "panelColor", current.PanelColor, out var panelColor))
        {
            error = "Colors must be six-digit hex values such as #F1F4F8.";
            return false;
        }

        updated = new OverlaySettings
        {
            DisplayMode = mode,
            ShowKeyboard = showKeyboard,
            ShowMouse = showMouse,
            OverlayVisible = overlayVisible,
            FadeMilliseconds = fadeMilliseconds,
            PositionX = positionX,
            PositionY = positionY,
            LayoutScalePercent = layoutScalePercent,
            FontFamily = fontFamily,
            FontSize = fontSize,
            TextColor = textColor,
            AccentColor = accentColor,
            KeyColor = keyColor,
            PanelColor = panelColor,
            PanelOpacity = panelOpacity
        };
        error = "";
        return true;
    }

    private static bool TryGetInt(
        JsonElement element,
        string name,
        ref int value,
        int minimum,
        int maximum,
        out string error)
    {
        error = "Invalid settings value.";
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (!property.TryGetInt32(out var parsed) || parsed < minimum || parsed > maximum)
        {
            error = $"{name} must be between {minimum} and {maximum}.";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetString(JsonElement element, string name, string current, out string value)
    {
        value = current;
        if (!element.TryGetProperty(name, out var property))
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } parsed)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetColor(JsonElement element, string name, string current, out string value)
    {
        if (!TryGetString(element, name, current, out value))
        {
            return false;
        }

        return !element.TryGetProperty(name, out _) || OverlaySettings.IsHexColor(value);
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

    private static byte[] Serialize<T>(T message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, BrowserClient.SerializerOptions);

    internal static bool IsAllowedOrigin(string origin, int port)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (uri.Port != port)
        {
            return false;
        }

        return uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    private void Broadcast(byte[] message)
    {
        var clients = _clients.ToArray();
        foreach (var item in clients)
        {
            if (item.Value.Socket.State != WebSocketState.Open || !item.Value.Send(message))
            {
                _clients.TryRemove(item.Key, out _);
                item.Value.Dispose();
            }
        }
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
        internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private readonly Channel<byte[]> _outbound;

        public BrowserClient(WebSocket socket, int queueCapacity)
        {
            Socket = socket;
            _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        public WebSocket Socket { get; }

        public bool Send(byte[] payload)
        {
            return _outbound.Writer.TryWrite(payload);
        }

        public async Task SendLoopAsync()
        {
            await foreach (var payload in _outbound.Reader.ReadAllAsync())
            {
                if (Socket.State != WebSocketState.Open)
                {
                    return;
                }

                await Socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }

        public void Dispose()
        {
            _outbound.Writer.TryComplete();
            Socket.Dispose();
        }
    }

    private sealed class PrefixedStream : Stream
    {
        private readonly MemoryStream _prefix;
        private readonly Stream _inner;

        public PrefixedStream(byte[] prefix, Stream inner)
        {
            _prefix = new MemoryStream(prefix);
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _prefix.Length + _inner.Length;
        public override long Position
        {
            get => _prefix.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefix.Position < _prefix.Length)
            {
                var read = _prefix.Read(buffer, offset, count);
                if (read > 0)
                {
                    return read;
                }
            }

            return _inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.Position < _prefix.Length)
            {
                var read = await _prefix.ReadAsync(buffer, cancellationToken);
                if (read > 0)
                {
                    return read;
                }
            }

            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_prefix.Position < _prefix.Length)
            {
                var read = _prefix.Read(buffer, offset, count);
                if (read > 0)
                {
                    return Task.FromResult(read);
                }
            }

            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed record BrowserRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers);
}
