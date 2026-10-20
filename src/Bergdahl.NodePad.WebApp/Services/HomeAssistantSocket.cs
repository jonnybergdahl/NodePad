using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Bergdahl.NodePad.WebApp.Services;

/// <summary>
/// A WebSocket connection to Home Assistant (/api/websocket) that subscribes to the items of todo
/// entities. Home Assistant sends the full item list right after subscribing and again after every change.
/// </summary>
public sealed class HomeAssistantSocket : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // Subscription message id -> entity, and entity -> list name
    private readonly ConcurrentDictionary<int, string> _subscriptions = new();
    private readonly ConcurrentDictionary<string, string> _lists = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private int _nextId;

    private HomeAssistantSocket(ILogger logger)
    {
        _logger = logger;
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Connects and signs in with the access token. Throws a <see cref="HomeAssistantException"/> if that fails.
    /// </summary>
    public static async Task<HomeAssistantSocket> ConnectAsync(string url, string token, ILogger logger, CancellationToken ct)
    {
        var socket = new HomeAssistantSocket(logger);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket._socket.ConnectAsync(SocketUri(url), timeout.Token);
            await socket.ExpectAsync("auth_required", timeout.Token);
            await socket.SendAsync(new { type = "auth", access_token = token }, timeout.Token);
            var answer = await socket.ReceiveAsync(timeout.Token);
            var type = answer == null ? null : HomeAssistantClient.String(answer.Value, "type");
            if (type == "auth_invalid") throw new HomeAssistantException("Home Assistant rejected the access token");
            if (type != "auth_ok") throw new HomeAssistantException("Home Assistant did not accept the connection");
            return socket;
        }
        catch (Exception ex) when ((ex is WebSocketException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            await socket.DisposeAsync();
            throw new HomeAssistantException($"Could not connect to Home Assistant: {ex.Message}");
        }
        catch
        {
            await socket.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The WebSocket address for a Home Assistant address, keeping any path prefix (a reverse proxy).
    /// </summary>
    internal static Uri SocketUri(string url)
    {
        var builder = new UriBuilder(new Uri(new Uri(url.TrimEnd('/') + "/"), "api/websocket"));
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    /// <summary>
    /// Subscribes to an entity's items once per connection; events are reported for the given list.
    /// </summary>
    public async Task SubscribeItemsAsync(string entityId, string listName, CancellationToken ct)
    {
        if (!_lists.TryAdd(entityId, listName))
        {
            _lists[entityId] = listName;
            return;
        }
        var id = Interlocked.Increment(ref _nextId);
        _subscriptions[id] = entityId;
        await SendAsync(new { id, type = "todo/item/subscribe", entity_id = entityId }, ct);
    }

    /// <summary>
    /// Drops what is known about an entity, for example when its list was deleted, so the next
    /// <see cref="SubscribeItemsAsync"/> subscribes again.
    /// </summary>
    public void Forget(string entityId)
    {
        _lists.TryRemove(entityId, out _);
        foreach (var subscription in _subscriptions.Where(s => s.Value == entityId).ToList())
        {
            _subscriptions.TryRemove(subscription.Key, out _);
        }
    }

    /// <summary>
    /// Reads messages until Home Assistant closes the connection, reporting item lists as (list name, items).
    /// </summary>
    public async Task RunAsync(Action<string, List<HomeAssistantClient.Item>> onItems, CancellationToken ct)
    {
        while (true)
        {
            var message = await ReceiveAsync(ct);
            if (message == null) return;
            var root = message.Value;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
            if (!_subscriptions.TryGetValue(id, out var entityId)) continue;

            switch (HomeAssistantClient.String(root, "type"))
            {
                case "event":
                    if (_lists.TryGetValue(entityId, out var listName)
                        && root.TryGetProperty("event", out var ev) && ev.TryGetProperty("items", out var items))
                    {
                        onItems(listName, HomeAssistantClient.ParseItems(items));
                    }
                    break;
                case "result" when root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False:
                    _logger.LogWarning("Home Assistant refused to report changes to {Entity}: {Error}", entityId, root.GetRawText());
                    Forget(entityId);
                    break;
            }
        }
    }

    private async Task ExpectAsync(string type, CancellationToken ct)
    {
        var message = await ReceiveAsync(ct);
        if (message == null || HomeAssistantClient.String(message.Value, "type") != type)
        {
            throw new HomeAssistantException("Home Assistant did not answer as expected on /api/websocket");
        }
    }

    private async Task SendAsync(object message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// The next message, or null once the connection is closed.
    /// </summary>
    private async Task<JsonElement?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        try
        {
            using var document = JsonDocument.Parse(message.ToArray());
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            _logger.LogWarning("Home Assistant sent a message that is not JSON: {Message}", Encoding.UTF8.GetString(message.ToArray()));
            return default(JsonElement);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Closing anyway
            }
        }
        _socket.Dispose();
        _sendLock.Dispose();
    }
}
